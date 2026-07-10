namespace SmtpRelay.Configuration;

/// <summary>Modalita TLS per il canale in ingresso (client -> relay).</summary>
public enum InboundTlsMode
{
    /// <summary>Nessun TLS: traffico in chiaro (solo rete interna fidata).</summary>
    None,

    /// <summary>STARTTLS offerto ma non obbligatorio.</summary>
    StartTlsOptional,

    /// <summary>STARTTLS obbligatorio: MAIL FROM rifiutato finche la sessione non e cifrata.</summary>
    StartTlsRequired
}

/// <summary>Configurazione TLS in ingresso.</summary>
public sealed class TlsOptions
{
    public InboundTlsMode Mode { get; set; } = InboundTlsMode.None;

    /// <summary>Percorso di un certificato PKCS#12 (.pfx). Alternativa a Thumbprint.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Nome del segreto (nello store cifrato) che contiene la password del .pfx.</summary>
    public string? CertificatePasswordSecret { get; set; }

    /// <summary>Thumbprint di un certificato in LocalMachine\My. Alternativa a CertificatePath.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Protocollo minimo: "Tls12" o "Tls13".</summary>
    public string MinimumProtocol { get; set; } = "Tls12";
}
