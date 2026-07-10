using System.Buffers;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace SmtpRelay.Smtp;

/// <summary>
/// Riceve il messaggio (comando DATA) e lo inoltra all'upstream. Registra l'esito nell'audit trail
/// e, in caso di errore transitorio, restituisce 451 lasciando che il client ritenti.
/// </summary>
public sealed class RelayMessageStore : MessageStore
{
    private readonly IUpstreamSender _sender;
    private readonly DeliveryOptions _delivery;
    private readonly string _failedSpoolDirectory;
    private readonly ILogger _logger;
    private readonly ILogger _audit;

    public RelayMessageStore(
        IUpstreamSender sender,
        DeliveryOptions delivery,
        string failedSpoolDirectory,
        ILogger logger,
        ILogger audit)
    {
        _sender = sender;
        _delivery = delivery;
        _failedSpoolDirectory = failedSpoolDirectory;
        _logger = logger;
        _audit = audit;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var clientIp = GetRemoteAddress(context);
        var envelopeFrom = ToAddress(transaction.From);
        var recipients = transaction.To.Select(ToAddress).Where(a => a.Length > 0).ToList();

        MimeMessage message;
        try
        {
            var data = buffer.ToArray();
            using var stream = new MemoryStream(data, writable: false);
            message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Impossibile analizzare il messaggio ricevuto da {ClientIp}.", clientIp);
            return new SmtpResponse(SmtpReplyCode.TransactionFailed, "5.6.0 messaggio non analizzabile");
        }

        var subject = message.Subject ?? "(nessun oggetto)";
        var sizeBytes = buffer.Length;

        try
        {
            await _sender.SendAsync(message, envelopeFrom, recipients, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _audit.LogInformation(
                "DELIVERED client={ClientIp} from={From} to={To} size={Size} subject={Subject} durationMs={Duration}",
                clientIp, NullIfEmpty(envelopeFrom), string.Join(",", recipients), sizeBytes, subject, stopwatch.ElapsedMilliseconds);

            return SmtpResponse.Ok;
        }
        catch (UpstreamDeliveryException ex)
        {
            stopwatch.Stop();
            _audit.LogWarning(
                "FAILED client={ClientIp} from={From} to={To} size={Size} subject={Subject} permanent={Permanent} reason={Reason}",
                clientIp, NullIfEmpty(envelopeFrom), string.Join(",", recipients), sizeBytes, subject, ex.IsPermanent, ex.Message);

            _logger.LogError(ex, "Consegna verso l'upstream fallita (permanente={Permanent}).", ex.IsPermanent);

            if (_delivery.SpoolFailedMessages)
                await SpoolAsync(message, clientIp, cancellationToken).ConfigureAwait(false);

            return ex.IsPermanent
                ? new SmtpResponse(SmtpReplyCode.TransactionFailed, "5.4.0 impossibile inoltrare il messaggio")
                : new SmtpResponse((SmtpReplyCode)451, "4.4.1 upstream non disponibile, riprovare piu tardi");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SmtpResponse((SmtpReplyCode)451, "4.3.0 servizio in arresto, riprovare");
        }
    }

    private async Task SpoolAsync(MimeMessage message, string clientIp, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_failedSpoolDirectory);
            var name = $"failed-{message.MessageId ?? Guid.NewGuid().ToString("N")}".Replace("@", "_").Replace(":", "_");
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            var path = Path.Combine(_failedSpoolDirectory, name + ".eml");
            await using var fs = File.Create(path);
            await message.WriteToAsync(fs, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Copia diagnostica del messaggio non consegnato salvata in {Path} (client {ClientIp}).", path, clientIp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile salvare la copia diagnostica del messaggio non consegnato.");
        }
    }

    private static string GetRemoteAddress(ISessionContext context)
        => context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) && value is IPEndPoint ep
            ? ep.Address.ToString()
            : "sconosciuto";

    private static string ToAddress(IMailbox? mailbox)
    {
        if (mailbox is null)
            return string.Empty;
        if (string.IsNullOrEmpty(mailbox.Host))
            return mailbox.User ?? string.Empty;
        return $"{mailbox.User}@{mailbox.Host}";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? "<>" : value;
}
