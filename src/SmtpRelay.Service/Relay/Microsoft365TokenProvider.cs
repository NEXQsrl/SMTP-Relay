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
/// Fornisce un access token per SMTP AUTH XOAUTH2 verso Microsoft 365 tramite il flusso
/// OAuth2 Client Credentials (app-only). MSAL mantiene una cache in memoria e rinnova
/// automaticamente il token prima della scadenza.
/// </summary>
public sealed class Microsoft365TokenProvider : IM365TokenProvider
{
    private readonly Microsoft365Options _options;
    private readonly SecretStore _secrets;
    private readonly ILogger<Microsoft365TokenProvider> _logger;
    private readonly object _sync = new();
    private IConfidentialClientApplication? _app;

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

        _logger.LogDebug("Token M365 ottenuto (fonte={Source}, scade={ExpiresOn:o})",
            result.AuthenticationResultMetadata.TokenSource, result.ExpiresOn);

        return result.AccessToken;
    }

    private IConfidentialClientApplication GetOrCreateApp()
    {
        if (_app is not null)
            return _app;

        lock (_sync)
        {
            if (_app is not null)
                return _app;

            var clientSecret = _secrets.Get(_options.ClientSecretName)
                ?? throw new InvalidOperationException(
                    $"Client secret M365 '{_options.ClientSecretName}' non presente nello store. " +
                    "Impostalo con: SmtpRelay set-credential --name " + _options.ClientSecretName + " --value <secret>");

            _app = ConfidentialClientApplicationBuilder
                .Create(_options.ClientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{_options.TenantId}"))
                .Build();

            return _app;
        }
    }
}
