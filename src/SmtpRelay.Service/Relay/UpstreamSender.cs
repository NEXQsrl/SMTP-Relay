using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Relay;

public interface IUpstreamSender
{
    /// <summary>Inoltra il messaggio all'upstream usando la busta SMTP fornita.</summary>
    Task SendAsync(MimeMessage message, string? envelopeFrom, IReadOnlyList<string> recipients, CancellationToken cancellationToken);

    /// <summary>Verifica connessione + autenticazione senza inviare (per il comando di test).</summary>
    Task VerifyConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Inoltra i messaggi allo smart host / Microsoft 365 tramite MailKit, con autenticazione
/// Basic oppure OAuth2 (M365) e retry con backoff lineare sugli errori transitori.
/// </summary>
public sealed class UpstreamSender : IUpstreamSender
{
    private readonly UpstreamOptions _options;
    private readonly SecretStore _secrets;
    private readonly IM365TokenProvider _tokenProvider;
    private readonly ILogger<UpstreamSender> _logger;

    public UpstreamSender(
        UpstreamOptions options,
        SecretStore secrets,
        IM365TokenProvider tokenProvider,
        ILogger<UpstreamSender> logger)
    {
        _options = options;
        _secrets = secrets;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task SendAsync(MimeMessage message, string? envelopeFrom, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        var sender = ResolveSender(envelopeFrom);
        var rcpts = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(MailboxAddress.Parse)
            .ToList();

        if (rcpts.Count == 0)
            throw new UpstreamDeliveryException("Nessun destinatario valido.", isPermanent: true);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await SendOnceAsync(message, sender, rcpts, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                bool transient = IsTransient(ex);
                if (transient && attempt < _options.MaxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * attempt);
                    _logger.LogWarning(ex,
                        "Tentativo {Attempt}/{Max} di consegna fallito (transitorio). Nuovo tentativo tra {Delay}s.",
                        attempt, _options.MaxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new UpstreamDeliveryException(
                    $"Consegna fallita dopo {attempt} tentativo/i: {ex.Message}",
                    isPermanent: !transient,
                    inner: ex);
            }
        }
    }

    public async Task VerifyConnectionAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        await client.ConnectAsync(_options.Host, _options.Port, MapSecure(_options.Security), cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(client, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendOnceAsync(MimeMessage message, MailboxAddress sender, IList<MailboxAddress> recipients, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        await client.ConnectAsync(_options.Host, _options.Port, MapSecure(_options.Security), cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(client, cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, sender, recipients, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private SmtpClient CreateClient() => new() { Timeout = _options.TimeoutSeconds * 1000 };

    private async Task AuthenticateAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        if (_options.Mode == UpstreamAuthMode.Basic)
        {
            var password = _secrets.Get(_options.Basic.PasswordSecret)
                ?? throw new UpstreamDeliveryException(
                    $"Password upstream '{_options.Basic.PasswordSecret}' non configurata. " +
                    "Impostala con: SmtpRelay set-credential --name " + _options.Basic.PasswordSecret + " --value <password>",
                    isPermanent: true);
            await client.AuthenticateAsync(_options.Basic.Username, password, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var oauth2 = new SaslMechanismOAuth2(_options.Microsoft365.SenderUsername, token);
            await client.AuthenticateAsync(oauth2, cancellationToken).ConfigureAwait(false);
        }
    }

    private MailboxAddress ResolveSender(string? envelopeFrom)
    {
        // Priorita: override esplicito -> return-path della busta -> identita di autenticazione.
        if (!string.IsNullOrWhiteSpace(_options.OverrideFromAddress))
            return MailboxAddress.Parse(_options.OverrideFromAddress);

        if (!string.IsNullOrWhiteSpace(envelopeFrom))
            return MailboxAddress.Parse(envelopeFrom);

        var identity = _options.Mode == UpstreamAuthMode.Basic
            ? _options.Basic.Username
            : _options.Microsoft365.SenderUsername;

        return MailboxAddress.Parse(identity);
    }

    private static SecureSocketOptions MapSecure(UpstreamSecureMode mode) => mode switch
    {
        UpstreamSecureMode.None => SecureSocketOptions.None,
        UpstreamSecureMode.Auto => SecureSocketOptions.Auto,
        UpstreamSecureMode.StartTls => SecureSocketOptions.StartTls,
        UpstreamSecureMode.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        UpstreamSecureMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto
    };

    private static bool IsTransient(Exception ex) => ex switch
    {
        SmtpCommandException sce => (int)sce.StatusCode is >= 400 and < 500,
        AuthenticationException => false,
        SmtpProtocolException => true,
        System.Net.Sockets.SocketException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => false,
        _ => false
    };
}
