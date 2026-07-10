namespace SmtpRelay.Configuration;

/// <summary>
/// Radice di configurazione del relay. Legata alla sezione "SmtpRelay" di appsettings.json.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "SmtpRelay";

    /// <summary>Nome del servizio Windows.</summary>
    public string ServiceName { get; set; } = "SmtpRelay";

    /// <summary>Cartella dati (log, segreti, spool). Deve essere scrivibile dall'account del servizio.</summary>
    public string DataDirectory { get; set; } = @"C:\ProgramData\SmtpRelay";

    public ListenerOptions Listener { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public UpstreamOptions Upstream { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public DeliveryOptions Delivery { get; set; } = new();

    public string SecretsFilePath => System.IO.Path.Combine(DataDirectory, "secrets.dat");
    public string EntropyFilePath => System.IO.Path.Combine(DataDirectory, "entropy.bin");
    public string LogDirectory => string.IsNullOrWhiteSpace(Logging.Directory)
        ? System.IO.Path.Combine(DataDirectory, "logs")
        : Logging.Directory!;
    public string FailedSpoolDirectory => string.IsNullOrWhiteSpace(Delivery.FailedSpoolDirectory)
        ? System.IO.Path.Combine(DataDirectory, "failed")
        : Delivery.FailedSpoolDirectory!;
}
