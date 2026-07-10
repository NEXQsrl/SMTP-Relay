using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Storage;

namespace SmtpRelay.Smtp;

/// <summary>
/// Filtro di busta: applica dimensione massima, numero massimo di destinatari e, se richiesto,
/// l'obbligo di STARTTLS prima di accettare il messaggio.
/// </summary>
public sealed class RelayMailboxFilter : MailboxFilter
{
    private const string RecipientCountKey = "SmtpRelay:RecipientCount";

    private readonly ListenerOptions _listener;
    private readonly InboundTlsMode _tlsMode;
    private readonly ILogger _logger;

    public RelayMailboxFilter(ListenerOptions listener, InboundTlsMode tlsMode, ILogger logger)
    {
        _listener = listener;
        _tlsMode = tlsMode;
        _logger = logger;
    }

    public override Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox @from, int size, CancellationToken cancellationToken)
    {
        if (_tlsMode == InboundTlsMode.StartTlsRequired && !context.Pipe.IsSecure)
        {
            _logger.LogWarning("MAIL FROM rifiutato: STARTTLS obbligatorio ma la sessione non e cifrata.");
            return Task.FromResult(false);
        }

        if (size > 0 && size > _listener.MaxMessageSizeBytes)
        {
            _logger.LogWarning("MAIL FROM rifiutato: dimensione dichiarata {Size} supera il limite {Max}.",
                size, _listener.MaxMessageSizeBytes);
            return Task.FromResult(false);
        }

        // Nuova transazione: azzera il contatore dei destinatari.
        context.Properties[RecipientCountKey] = 0;
        return Task.FromResult(true);
    }

    public override Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox @from, CancellationToken cancellationToken)
    {
        int count = context.Properties.TryGetValue(RecipientCountKey, out var value) && value is int i ? i : 0;
        count++;
        context.Properties[RecipientCountKey] = count;

        if (count > _listener.MaxRecipients)
        {
            _logger.LogWarning("RCPT TO rifiutato: superato il numero massimo di destinatari ({Max}).", _listener.MaxRecipients);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
