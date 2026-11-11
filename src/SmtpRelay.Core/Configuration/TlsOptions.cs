namespace SmtpRelay.Configuration;

/// <summary>TLS mode of the inbound channel (client -> relay).</summary>
public enum InboundTlsMode
{
    /// <summary>No TLS: clear text (trusted internal network only).</summary>
    None,

    /// <summary>STARTTLS offered but not required.</summary>
    StartTlsOptional,

    /// <summary>STARTTLS required: MAIL FROM is refused until the session is encrypted.</summary>
    StartTlsRequired
}

/// <summary>Inbound TLS settings.</summary>
public sealed class TlsOptions
{
    public InboundTlsMode Mode { get; set; } = InboundTlsMode.None;

    /// <summary>Path of a PKCS#12 certificate (.pfx). Alternative to the thumbprint.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Name of the secret (in the encrypted store) that holds the password of the .pfx.</summary>
    public string? CertificatePasswordSecret { get; set; }

    /// <summary>Thumbprint of a certificate in LocalMachine\My. Alternative to CertificatePath.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Minimum protocol: "Tls12" or "Tls13".</summary>
    public string MinimumProtocol { get; set; } = "Tls12";
}
