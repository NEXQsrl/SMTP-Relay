namespace SmtpRelay.Configuration;

/// <summary>Configurazione del logging con retention.</summary>
public sealed class LoggingOptions
{
    /// <summary>Cartella dei log. Se vuota usa {DataDirectory}\logs.</summary>
    public string? Directory { get; set; }

    /// <summary>Numero massimo di file di log conservati (rotazione giornaliera + per dimensione).</summary>
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>Giorni massimi di conservazione dei log. Null = nessun limite temporale.</summary>
    public int? RetainedFileTimeLimitDays { get; set; } = 31;

    /// <summary>Dimensione massima per file di log prima della rotazione (byte).</summary>
    public long FileSizeLimitBytes { get; set; } = 52_428_800; // 50 MB

    /// <summary>Livello minimo: Verbose, Debug, Information, Warning, Error, Fatal.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Scrive anche su console (utile in modalita interattiva/console).</summary>
    public bool Console { get; set; } = true;

    /// <summary>Abilita un file di audit separato per gli eventi di consegna.</summary>
    public bool AuditTrail { get; set; } = true;
}
