using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Relay;

public interface IM365TokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provides the access token for SMTP AUTH XOAUTH2 towards Microsoft 365 with the OAuth2 client credentials
/// flow (app-only). MSAL caches the token in memory and renews it before it expires.
/// </summary>
public sealed class Microsoft365TokenProvider : IM365TokenProvider
{
    private readonly Microsoft365Options _options;
    private readonly SecretStore _secrets;
    private readonly ILogger<Microsoft365TokenProvider> _logger;
    private readonly object _sync = new();
    private IConfidentialClientApplication? _app;
    private string? _appSecret;

    public Microsoft365TokenProvider(
        Microsoft365Options options,
        SecretStore secrets,
        ILogger<Microsoft365TokenProvider> logger)
    {
        _options = options;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var app = GetOrCreateApp();
        var result = await app
            .AcquireTokenForClient(new[] { _options.Scope })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Microsoft 365 token acquired (source={Source}, expires={ExpiresOn:o})",
            result.AuthenticationResultMetadata.TokenSource, result.ExpiresOn);

        return result.AccessToken;
    }

    private IConfidentialClientApplication GetOrCreateApp()
    {
        var clientSecret = _secrets.Get(_options.ClientSecretName)
            ?? throw new UpstreamDeliveryException(
                $"The Microsoft 365 client secret '{_options.ClientSecretName}' is not in the secret store. " +
                "Set it in the management console, or with: SmtpRelay set-credential --name " + _options.ClientSecretName,
                isPermanent: true);

        lock (_sync)
        {
            // Rebuilt when the secret is rotated, so that the service does not need a restart.
            if (_app is not null && string.Equals(_appSecret, clientSecret, StringComparison.Ordinal))
                return _app;

            _app = ConfidentialClientApplicationBuilder
                .Create(_options.ClientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(_options.TenantId)}"))
                .Build();
            _appSecret = clientSecret;
            return _app;
        }
    }
}
