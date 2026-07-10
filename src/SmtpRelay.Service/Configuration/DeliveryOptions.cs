namespace SmtpRelay.Configuration;

/// <summary>Parametri della consegna in uscita.</summary>
public sealed class DeliveryOptions
{
    /// <summary>Se true, in caso di fallimento definitivo salva una copia .eml a scopo diagnostico.</summary>
    public bool SpoolFailedMessages { get; set; } = true;

    /// <summary>Cartella dello spool dei falliti. Se vuota usa {DataDirectory}\failed.</summary>
    public string? FailedSpoolDirectory { get; set; }
}
