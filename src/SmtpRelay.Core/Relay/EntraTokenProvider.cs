using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Relay;

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provides the access token for SMTP AUTH XOAUTH2 (Microsoft 365 or Azure Communication Services) with the
/// OAuth2 client credentials flow (app-only). The application proves its identity with a client secret or with
/// a certificate. MSAL caches the token in memory and renews it before it expires.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EntraTokenProvider : IAccessTokenProvider, IDisposable
{
    private readonly UpstreamOptions _upstream;
    private readonly SecretStore _secrets;
    private readonly ILogger<EntraTokenProvider> _logger;
    private readonly object _sync = new();
    private IConfidentialClientApplication? _app;
    private string? _appKey;
    private X509Certificate2? _certificate;

    public EntraTokenProvider(UpstreamOptions upstream, SecretStore secrets, ILogger<EntraTokenProvider> logger)
    {
        _upstream = upstream;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var options = _upstream.EntraApplication
            ?? throw new UpstreamDeliveryException(
                $"Authentication mode {_upstream.Mode} does not use a Microsoft Entra application.", isPermanent: true);

        var app = GetOrCreateApp(options);
        var result = await app
            .AcquireTokenForClient(new[] { options.Scope })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug("Access token acquired for {Scope} (source={Source}, expires={ExpiresOn:o})",
            options.Scope, result.AuthenticationResultMetadata.TokenSource, result.ExpiresOn);

        return result.AccessToken;
    }

    private IConfidentialClientApplication GetOrCreateApp(EntraApplicationOptions options)
    {
        if (options.Credential == EntraCredentialType.Certificate)
        {
            lock (_sync)
            {
                var key = "certificate:" + CertificateFinder.Normalize(options.CertificateThumbprint);
                // The certificate is looked up again once it has expired, so that a renewed one with the same
                // thumbprint configured after a restart, or a clear error, is what the administrator gets.
                if (_app is not null && _appKey == key && _certificate is not null && _certificate.NotAfter > DateTime.Now)
                    return _app;

                var certificate = LoadCertificate(options.CertificateThumbprint);
                _certificate?.Dispose();
                _certificate = certificate;
                _app = NewBuilder(options).WithCertificate(certificate).Build();
                _appKey = key;
                return _app;
            }
        }

        var clientSecret = _secrets.Get(options.ClientSecretName)
            ?? throw new UpstreamDeliveryException(
                $"The client secret '{options.ClientSecretName}' is not in the secret store. " +
                "Set it in the management console, or with: SmtpRelay set-credential --name " + options.ClientSecretName,
                isPermanent: true);

        lock (_sync)
        {
            // Rebuilt when the secret is rotated, so that the service does not need a restart.
            // An ordinal comparison of the value: the key never leaves this object.
            if (_app is not null && string.Equals(_appKey, "secret:" + clientSecret, StringComparison.Ordinal))
                return _app;

            _app = NewBuilder(options).WithClientSecret(clientSecret).Build();
            _appKey = "secret:" + clientSecret;
            return _app;
        }
    }

    private static ConfidentialClientApplicationBuilder NewBuilder(EntraApplicationOptions options)
        => ConfidentialClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(options.TenantId)}"));

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        var certificate = CertificateFinder.FindInLocalMachine(thumbprint)
            ?? throw new UpstreamDeliveryException(
                $"No certificate with thumbprint {CertificateFinder.Normalize(thumbprint)} in the LocalMachine\\My store.",
                isPermanent: true);

        var problem = CertificateFinder.DescribeProblem(certificate);
        if (problem is not null)
        {
            certificate.Dispose();
            throw new UpstreamDeliveryException(problem, isPermanent: true);
        }

        return certificate;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _certificate?.Dispose();
            _certificate = null;
            _app = null;
        }
    }
}
