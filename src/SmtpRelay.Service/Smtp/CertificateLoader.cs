using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Smtp;

/// <summary>Loads the TLS certificate of the listener from the LocalMachine\My store or from a .pfx file.</summary>
[SupportedOSPlatform("windows")]
public static class CertificateLoader
{
    public static X509Certificate2? Load(TlsOptions tls, SecretStore secrets, ILogger logger)
    {
        if (tls.Mode == InboundTlsMode.None)
            return null;

        X509Certificate2? certificate = null;

        if (!string.IsNullOrWhiteSpace(tls.CertificateThumbprint))
        {
            certificate = CertificateFinder.FindInLocalMachine(tls.CertificateThumbprint);
            if (certificate is null)
                logger.LogWarning("No certificate with thumbprint {Thumbprint} in LocalMachine\\My.", tls.CertificateThumbprint);
        }

        if (certificate is null && !string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            if (!File.Exists(tls.CertificatePath))
                throw new FileNotFoundException($"TLS certificate not found: {tls.CertificatePath}");

            var password = string.IsNullOrWhiteSpace(tls.CertificatePasswordSecret)
                ? null
                : secrets.Get(tls.CertificatePasswordSecret);

            // MachineKeySet without PersistKeySet: the private key is removed from the machine key store
            // when the certificate is disposed, instead of leaving one more key file behind at every start.
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                tls.CertificatePath!,
                password,
                X509KeyStorageFlags.MachineKeySet);
        }

        if (certificate is null)
            throw new InvalidOperationException("Inbound TLS is on but no certificate could be loaded: check Tls.CertificateThumbprint or Tls.CertificatePath.");

        if (!certificate.HasPrivateKey)
        {
            var subject = certificate.Subject;
            certificate.Dispose();
            throw new InvalidOperationException(
                $"The TLS certificate '{subject}' has no private key, or the service account cannot read it.");
        }

        var now = DateTime.Now;
        if (certificate.NotAfter < now)
            logger.LogError("The TLS certificate {Subject} EXPIRED on {NotAfter:d}: clients that verify it will refuse the connection.", certificate.Subject, certificate.NotAfter);
        else if (certificate.NotAfter < now.AddDays(30))
            logger.LogWarning("The TLS certificate {Subject} expires on {NotAfter:d}.", certificate.Subject, certificate.NotAfter);
        else
            logger.LogInformation("TLS certificate loaded: {Subject} (expires {NotAfter:d}).", certificate.Subject, certificate.NotAfter);

        return certificate;
    }
}
