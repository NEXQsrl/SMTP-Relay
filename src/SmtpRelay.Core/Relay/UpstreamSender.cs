using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using MimeKit;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Relay;

public interface IUpstreamSender
{
    /// <summary>Forwards the message to the smart host using the given SMTP envelope.</summary>
    Task SendAsync(MimeMessage message, string? envelopeFrom, IReadOnlyList<string> recipients, CancellationToken cancellationToken);

    /// <summary>Connects and authenticates without sending anything (used by the connection test).</summary>
    Task VerifyConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Forwards messages to the smart host and retries transient errors with a linear back-off. SMTP servers are
/// reached with MailKit, authenticating with Basic or OAuth2 (Microsoft 365); Azure Communication Services is
/// reached through its Email API (<see cref="AcsEmailClient"/>).
/// </summary>
public sealed class UpstreamSender : IUpstreamSender
{
    private readonly UpstreamOptions _options;
    private readonly SecretStore _secrets;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly ILogger<UpstreamSender> _logger;
    private readonly AcsEmailClient _acs;

    // Azure may ask to wait before the next request (throttling): it is obeyed up to this long, because the
    // client is waiting for its answer meanwhile.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public UpstreamSender(
        UpstreamOptions options,
        SecretStore secrets,
        IAccessTokenProvider tokenProvider,
        ILogger<UpstreamSender> logger)
    {
        _options = options;
        _secrets = secrets;
        _tokenProvider = tokenProvider;
        _logger = logger;
        _acs = new AcsEmailClient(options, secrets, logger);
    }

    /// <summary>
    /// Where the mail goes, for logs and messages: host and port of the SMTP server, or the endpoint of the
    /// Azure Communication Services resource (read from the stored connection string, when there is one).
    /// </summary>
    public static string DescribeTarget(UpstreamOptions options, SecretStore? secrets)
    {
        if (options.UsesSmtp)
            return $"{options.Host}:{options.Port}";

        string? stored = null;
        try { stored = secrets?.Get(options.AzureCommunication.ConnectionStringSecret); }
        catch (Exception) { /* a store that cannot be read is reported where it matters, not here */ }
        return AcsConnectionString.TryParse(stored, out var connection, out _)
            ? connection.Endpoint.Host
            : "Azure Communication Services";
    }

    public async Task SendAsync(MimeMessage message, string? envelopeFrom, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        MailboxAddress sender;
        List<MailboxAddress> rcpts;
        try
        {
            sender = ResolveSender(message, envelopeFrom);
            rcpts = recipients
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(MailboxAddress.Parse)
                .ToList();
        }
        catch (ParseException ex)
        {
            throw new UpstreamDeliveryException("Invalid address in the SMTP envelope: " + ex.Message, isPermanent: true, inner: ex);
        }

        if (rcpts.Count == 0)
            throw new UpstreamDeliveryException("No valid recipient.", isPermanent: true);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await SendOnceAsync(message, sender, rcpts, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UpstreamDeliveryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool transient = IsTransient(ex);
                if (transient && attempt < _options.MaxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(_options.RetryBaseDelaySeconds * attempt);
                    if (ex is AcsRequestException { RetryAfter: { } retryAfter } && retryAfter > delay)
                        delay = retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter;
                    _logger.LogWarning(
                        "Delivery attempt {Attempt}/{Max} failed (transient): {Reason}. Retrying in {Delay}s.",
                        attempt, _options.MaxAttempts, LogSanitizer.Clean(ex.Message), delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new UpstreamDeliveryException(
                    $"Delivery failed after {attempt} attempt(s): {ex.Message}",
                    isPermanent: !transient,
                    inner: ex);
            }
        }
    }

    public async Task VerifyConnectionAsync(CancellationToken cancellationToken)
    {
        if (!_options.UsesSmtp)
        {
            await _acs.VerifyAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var client = CreateClient();
        await client.ConnectAsync(_options.Host.Trim(), _options.Port, MapSecure(_options), cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(client, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendOnceAsync(MimeMessage message, MailboxAddress sender, List<MailboxAddress> recipients, CancellationToken cancellationToken)
    {
        if (!_options.UsesSmtp)
        {
            await _acs.SendAsync(message, sender, recipients, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var client = CreateClient();
        await client.ConnectAsync(_options.Host.Trim(), _options.Port, MapSecure(_options), cancellationToken).ConfigureAwait(false);
        await AuthenticateAsync(client, cancellationToken).ConfigureAwait(false);
        await client.SendAsync(message, sender, recipients, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private SmtpClient CreateClient() => new() { Timeout = _options.TimeoutSeconds * 1000 };

    private async Task AuthenticateAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        switch (_options.Mode)
        {
            case UpstreamAuthMode.Anonymous:
                return;

            case UpstreamAuthMode.Basic:
                RequireEncryptedChannel(client);
                var password = _secrets.Get(_options.Basic.PasswordSecret)
                    ?? throw new UpstreamDeliveryException(
                        $"The smart host password '{_options.Basic.PasswordSecret}' is not in the secret store. " +
                        "Set it in the management console, or with: SmtpRelay set-credential --name " + _options.Basic.PasswordSecret,
                        isPermanent: true);
                await client.AuthenticateAsync(_options.Basic.Username, password, cancellationToken).ConfigureAwait(false);
                return;

            default:
                RequireEncryptedChannel(client);
                var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                var oauth2 = new SaslMechanismOAuth2(_options.TokenUserName ?? "", token);
                await client.AuthenticateAsync(oauth2, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    // Last line of defence against a STARTTLS downgrade: credentials never leave on a clear-text channel
    // unless the administrator explicitly allowed it.
    private void RequireEncryptedChannel(SmtpClient client)
    {
        if (!client.IsSecure && !_options.AllowInsecureAuthentication)
            throw new UpstreamDeliveryException(
                "The connection to the smart host is not encrypted: credentials were not sent. " +
                "Use StartTls or SslOnConnect, or set Upstream.AllowInsecureAuthentication to true.",
                isPermanent: true);
    }

    private MailboxAddress ResolveSender(MimeMessage message, string? envelopeFrom)
    {
        // Priority: explicit override -> return-path of the envelope -> authentication identity.
        if (!string.IsNullOrWhiteSpace(_options.OverrideFromAddress))
            return MailboxAddress.Parse(_options.OverrideFromAddress);

        // Azure Communication Services has one sender only, and it is the From the recipient reads: the author
        // of the message comes before the return-path.
        if (!_options.UsesSmtp && message.From.Mailboxes.FirstOrDefault() is { } author)
            return author;

        if (!string.IsNullOrWhiteSpace(envelopeFrom))
            return MailboxAddress.Parse(envelopeFrom);

        var identity = _options.Mode switch
        {
            UpstreamAuthMode.Basic => _options.Basic.Username,
            UpstreamAuthMode.Microsoft365 => _options.Microsoft365.SenderUsername,
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(identity) || !identity.Contains('@'))
            throw new UpstreamDeliveryException(
                "The message has an empty envelope sender and no default sender is configured (Upstream.OverrideFromAddress).",
                isPermanent: true);

        return MailboxAddress.Parse(identity);
    }

    internal static SecureSocketOptions MapSecure(UpstreamOptions options) => options.Security switch
    {
        UpstreamSecureMode.None => SecureSocketOptions.None,
        UpstreamSecureMode.StartTls => SecureSocketOptions.StartTls,
        UpstreamSecureMode.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
        UpstreamSecureMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        // Auto never falls back to clear text (MailKit's own Auto would, on ports other than 465).
        _ => options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls
    };

    internal static bool IsTransient(Exception ex) => ex switch
    {
        SmtpCommandException sce => (int)sce.StatusCode is >= 400 and < 500,
        AuthenticationException => false,
        SslHandshakeException => false,
        SmtpProtocolException => true,
        MailKit.ServiceNotConnectedException => true,
        MsalServiceException mse => mse.StatusCode is >= 500 or 429 or 408,
        MsalClientException => false,
        AcsRequestException acs => acs.IsTransient,
        HttpRequestException => true,
        System.Net.Sockets.SocketException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => true, // not requested by the caller (handled earlier): a timeout
        _ => false
    };
}
