using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpRelay.Security;

namespace SmtpRelay.Smtp;

/// <summary>Carica il certificato TLS del listener da file .pfx o dallo store LocalMachine\My.</summary>
[SupportedOSPlatform("windows")]
public static class CertificateLoader
{
    public static X509Certificate2? Load(TlsOptions tls, SecretStore secrets, ILogger logger)
    {
        if (tls.Mode == InboundTlsMode.None)
            return null;

        if (!string.IsNullOrWhiteSpace(tls.CertificateThumbprint))
        {
            var cert = LoadFromStore(tls.CertificateThumbprint!, logger);
            if (cert is not null)
                return cert;
            logger.LogWarning("Certificato con thumbprint {Thumbprint} non trovato in LocalMachine\\My.", tls.CertificateThumbprint);
        }

        if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            if (!File.Exists(tls.CertificatePath))
                throw new FileNotFoundException($"Certificato TLS non trovato: {tls.CertificatePath}");

            var password = string.IsNullOrWhiteSpace(tls.CertificatePasswordSecret)
                ? null
                : secrets.Get(tls.CertificatePasswordSecret);

            var cert = X509CertificateLoader.LoadPkcs12FromFile(
                tls.CertificatePath!,
                password,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

            logger.LogInformation("Certificato TLS caricato da file: {Subject} (scade {NotAfter:d})", cert.Subject, cert.NotAfter);
            return cert;
        }

        throw new InvalidOperationException("Tls.Mode diverso da None ma nessun certificato configurato.");
    }

    private static X509Certificate2? LoadFromStore(string thumbprint, ILogger logger)
    {
        var normalized = thumbprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        if (found.Count == 0)
            return null;

        var cert = found[0];
        logger.LogInformation("Certificato TLS caricato dallo store: {Subject} (scade {NotAfter:d})", cert.Subject, cert.NotAfter);
        return cert;
    }
}
