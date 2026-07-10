namespace SmtpRelay.Configuration;

/// <summary>Parametri del listener SMTP in ingresso.</summary>
public sealed class ListenerOptions
{
    /// <summary>Indirizzo IP su cui mettersi in ascolto. "0.0.0.0" = tutte le interfacce IPv4.</summary>
    public string IpAddress { get; set; } = "0.0.0.0";

    /// <summary>Porta di ascolto (impostabile dai settings).</summary>
    public int Port { get; set; } = 2525;

    /// <summary>Nome annunciato nel banner/EHLO.</summary>
    public string ServerName { get; set; } = "smtp-relay";

    /// <summary>Dimensione massima del messaggio in byte.</summary>
    public long MaxMessageSizeBytes { get; set; } = 26_214_400; // 25 MB

    /// <summary>Numero massimo di destinatari per messaggio.</summary>
    public int MaxRecipients { get; set; } = 100;

    /// <summary>Timeout di attesa comando (secondi).</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;
}
