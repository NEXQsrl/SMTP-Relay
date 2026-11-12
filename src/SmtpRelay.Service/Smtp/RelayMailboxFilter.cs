using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace SmtpRelay.Smtp;

/// <summary>
/// Envelope filter: enforces the maximum size, the maximum number of recipients and, when required,
/// STARTTLS and the sign-in before a message is accepted.
/// </summary>
public sealed class RelayMailboxFilter : MailboxFilter
{
    private const string RecipientCountKey = "SmtpRelay:RecipientCount";

    private readonly ListenerOptions _listener;
    private readonly InboundTlsMode _tlsMode;
    private readonly InboundAuthMode _authMode;
    private readonly ILogger _logger;
    private readonly ThrottledLog _notSignedInLog = new(TimeSpan.FromMinutes(1));

    public RelayMailboxFilter(ListenerOptions listener, InboundTlsMode tlsMode, ILogger logger, InboundAuthMode authMode = InboundAuthMode.Disabled)
    {
        _listener = listener;
        _tlsMode = tlsMode;
        _authMode = authMode;
        _logger = logger;
    }

    public override Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox @from, int size, CancellationToken cancellationToken)
    {
        if (_tlsMode == InboundTlsMode.StartTlsRequired && !context.Pipe.IsSecure)
        {
            _logger.LogWarning("MAIL FROM refused: STARTTLS is required but the session is not encrypted.");
            return Task.FromResult(false);
        }

        // A client must have signed in when everyone has to, or when it connected from outside the allowlist.
        bool mustSignIn = _authMode == InboundAuthMode.Required || context.Properties.ContainsKey(SessionKeys.MustAuthenticate);
        if (mustSignIn && SessionKeys.GetUser(context) is null)
        {
            var address = SessionKeys.GetRemoteAddress(context)?.ToString() ?? "unknown";
            if (_notSignedInLog.ShouldLog(address, out var suppressed))
            {
                _logger.LogWarning(
                    "MAIL FROM refused: {RemoteIp} has not signed in ({Suppressed} more refused since the last entry).",
                    address, suppressed);
            }
            throw new SmtpResponseException(new SmtpResponse((SmtpReplyCode)530, "5.7.0 Authentication required"));
        }

        if (size > 0 && size > _listener.MaxMessageSizeBytes)
        {
            _logger.LogWarning("MAIL FROM refused: the declared size {Size} exceeds the limit {Max}.",
                size, _listener.MaxMessageSizeBytes);
            return Task.FromResult(false);
        }

        // New transaction: reset the recipient counter.
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
            _logger.LogWarning("RCPT TO refused: more than {Max} recipients.", _listener.MaxRecipients);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
