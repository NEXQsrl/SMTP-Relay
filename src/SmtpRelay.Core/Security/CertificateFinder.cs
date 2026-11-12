using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SmtpRelay.Security;

/// <summary>Finds and creates certificates in the LocalMachine\My store.</summary>
[SupportedOSPlatform("windows")]
public static class CertificateFinder
{
    /// <summary>Thumbprint as upper-case hexadecimal digits only (drops spaces and the invisible characters a copy from the certificate dialog brings along).</summary>
    public static string Normalize(string? thumbprint)
        => new string((thumbprint ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>True when the text is a SHA-1 thumbprint (40 hexadecimal digits, separators ignored).</summary>
    public static bool IsThumbprint(string? thumbprint) => Normalize(thumbprint).Length == 40;

    public static X509Certificate2? FindInLocalMachine(string? thumbprint)
    {
        var normalized = Normalize(thumbprint);
        if (normalized.Length == 0)
            return null;

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Why the certificate cannot be used to sign, or null when it can.</summary>
    public static string? DescribeProblem(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        if (certificate.NotAfter < now)
            return $"The certificate '{certificate.Subject}' expired on {certificate.NotAfter:yyyy-MM-dd}.";
        if (certificate.NotBefore > now)
            return $"The certificate '{certificate.Subject}' is not valid before {certificate.NotBefore:yyyy-MM-dd}.";
        if (!certificate.HasPrivateKey)
            return $"The certificate '{certificate.Subject}' has no private key.";

        try
        {
            // HasPrivateKey is true even when the account cannot open the key: opening it is the only real check.
            using var rsa = certificate.GetRSAPrivateKey();
            using var ecdsa = rsa is null ? certificate.GetECDsaPrivateKey() : null;
            if (rsa is null && ecdsa is null)
                return $"The certificate '{certificate.Subject}' must have an RSA or ECDSA key.";
        }
        catch (CryptographicException ex)
        {
            return $"The private key of the certificate '{certificate.Subject}' cannot be read by this account: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// Creates a self-signed certificate in LocalMachine\My for a Microsoft Entra application (RSA 3072, SHA-256,
    /// private key not exportable). The public part is what gets uploaded to the app registration.
    /// </summary>
    /// <returns>The certificate as stored (the caller disposes it).</returns>
    public static X509Certificate2 CreateForEntraApplication(string subjectName, int validYears)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=" + subjectName.Replace("\"", "").Replace(",", " ")),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        using var ephemeral = request.CreateSelfSigned(notBefore, notBefore.AddYears(validYears));

        // Re-imported so that the private key is persisted in the machine key store, and is not exportable.
        var pfx = ephemeral.Export(X509ContentType.Pkcs12);
        var stored = X509CertificateLoader.LoadPkcs12(
            pfx, null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        CryptographicOperations.ZeroMemory(pfx);

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(stored);
        return stored;
    }
}
