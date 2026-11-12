using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace SmtpRelay.Smtp;

/// <summary>
/// Receives the message (DATA command) and forwards it to the smart host. The outcome goes to the audit
/// trail, with the address of the client and the relay user that signed in; on a transient error the client gets 451 and is expected to try again.
/// </summary>
public sealed class RelayMessageStore : MessageStore
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    private readonly IUpstreamSender _sender;
    private readonly DeliveryOptions _delivery;
    private readonly string _failedSpoolDirectory;
    private readonly ILogger _logger;
    private readonly ILogger _audit;
    private readonly DeliveryCounters _counters;
    private long _nextCleanupTicks;

    public RelayMessageStore(
        IUpstreamSender sender,
        DeliveryOptions delivery,
        string failedSpoolDirectory,
        ILogger logger,
        ILogger audit,
        DeliveryCounters counters)
    {
        _counters = counters;
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
        var user = SessionKeys.GetUser(context) ?? "-"; // "-" when the client did not sign in
        var envelopeFrom = ToAddress(transaction.From);
        var recipients = transaction.To.Select(ToAddress).Where(a => a.Length > 0).ToList();
        var from = envelopeFrom.Length == 0 ? "<>" : LogSanitizer.Clean(envelopeFrom);
        var to = LogSanitizer.Clean(string.Join(",", recipients), 1024);
        var sizeBytes = buffer.Length;

        MimeMessage message;
        try
        {
            var data = buffer.ToArray();
            using var stream = new MemoryStream(data, writable: false);
            message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SmtpResponse((SmtpReplyCode)451, "4.3.0 Service shutting down, try again later");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot parse the message received from {ClientIp}.", clientIp);
            _counters.RecordFailed();
            _audit.LogWarning(
                "FAILED client={ClientIp} user={User} from={From} to={To} size={Size} permanent={Permanent} reason={Reason}",
                clientIp, user, from, to, sizeBytes, true, "message cannot be parsed");
            return new SmtpResponse(SmtpReplyCode.TransactionFailed, "5.6.0 Message cannot be parsed");
        }

        using (message)
        {
            var subject = LogSanitizer.Clean(message.Subject ?? "(no subject)");
            try
            {
                await _sender.SendAsync(message, envelopeFrom, recipients, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _counters.RecordDelivered();
                _audit.LogInformation(
                    "DELIVERED client={ClientIp} user={User} from={From} to={To} size={Size} subject={Subject} durationMs={Duration}",
                    clientIp, user, from, to, sizeBytes, subject, stopwatch.ElapsedMilliseconds);

                return SmtpResponse.Ok;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _counters.RecordFailed();
                _audit.LogWarning(
                    "FAILED client={ClientIp} user={User} from={From} to={To} size={Size} subject={Subject} permanent={Permanent} reason={Reason}",
                    clientIp, user, from, to, sizeBytes, subject, false, "service shutting down");
                return new SmtpResponse((SmtpReplyCode)451, "4.3.0 Service shutting down, try again later");
            }
            catch (Exception ex)
            {
                // Anything that is not a classified delivery error is treated as transient: the client retries
                // and nothing is lost because of a defect on our side.
                bool permanent = ex is UpstreamDeliveryException { IsPermanent: true };
                stopwatch.Stop();

                _counters.RecordFailed();
                _audit.LogWarning(
                    "FAILED client={ClientIp} user={User} from={From} to={To} size={Size} subject={Subject} permanent={Permanent} reason={Reason}",
                    clientIp, user, from, to, sizeBytes, subject, permanent, LogSanitizer.Clean(ex.Message, 512));

                _logger.LogError(ex, "Delivery to the smart host failed (permanent={Permanent}).", permanent);

                if (_delivery.SpoolFailedMessages)
                    await SpoolAsync(message, clientIp).ConfigureAwait(false);

                return permanent
                    ? new SmtpResponse(SmtpReplyCode.TransactionFailed, "5.4.0 Message could not be relayed")
                    : new SmtpResponse((SmtpReplyCode)451, "4.4.1 Smart host not available, try again later");
            }
        }
    }

    private async Task SpoolAsync(MimeMessage message, string clientIp)
    {
        try
        {
            Directory.CreateDirectory(_failedSpoolDirectory);
            CleanupSpoolIfDue();

            // The same message sent again by a client that retries replaces its previous copy.
            var id = message.MessageId ?? Guid.NewGuid().ToString("N");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0, 8).ToLowerInvariant();
            var path = Path.Combine(_failedSpoolDirectory, $"failed-{DateTime.UtcNow:yyyyMMdd}-{hash}.eml");

            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                await message.WriteToAsync(fs, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("Copy of the undelivered message saved in {Path} (client {ClientIp}).", path, clientIp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot save the copy of the undelivered message.");
        }
    }

    // Keeps the spool from growing without limit: old copies are removed a few times a day.
    private void CleanupSpoolIfDue()
    {
        if (_delivery.FailedSpoolRetentionDays <= 0)
            return;

        var now = DateTime.UtcNow.Ticks;
        var due = Interlocked.Read(ref _nextCleanupTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextCleanupTicks, now + CleanupInterval.Ticks, due) != due)
            return;

        var limit = DateTime.UtcNow.AddDays(-_delivery.FailedSpoolRetentionDays);
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(_failedSpoolDirectory, "failed-*.eml"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (removed > 0)
            _logger.LogInformation("Removed {Count} spooled message(s) older than {Days} days.", removed, _delivery.FailedSpoolRetentionDays);
    }

    private static string GetRemoteAddress(ISessionContext context)
        => context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) && value is IPEndPoint ep
            ? ep.Address.ToString()
            : "unknown";

    private static string ToAddress(IMailbox? mailbox)
    {
        if (mailbox is null)
            return string.Empty;
        if (string.IsNullOrEmpty(mailbox.Host))
            return mailbox.User ?? string.Empty;
        return $"{mailbox.User}@{mailbox.Host}";
    }
}
