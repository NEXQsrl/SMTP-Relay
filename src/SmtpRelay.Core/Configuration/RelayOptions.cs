using System.Text.Json.Serialization;

namespace SmtpRelay.Configuration;

/// <summary>Root of the relay configuration. Bound to the "SmtpRelay" section of appsettings.json.</summary>
public sealed class RelayOptions
{
    public const string SectionName = "SmtpRelay";

    /// <summary>Name of the Windows service.</summary>
    public string ServiceName { get; set; } = "SmtpRelay";

    /// <summary>Data folder (logs, secrets, spool). The service account must be able to write to it.</summary>
    public string DataDirectory { get; set; } = @"C:\ProgramData\SmtpRelay";

    public ListenerOptions Listener { get; set; } = new();
    public TlsOptions Tls { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public UpstreamOptions Upstream { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public DeliveryOptions Delivery { get; set; } = new();
    public UpdateOptions Updates { get; set; } = new();

    [JsonIgnore] public string SecretsFilePath => Path.Combine(DataDirectory, "secrets.dat");
    [JsonIgnore] public string EntropyFilePath => Path.Combine(DataDirectory, "entropy.bin");
    [JsonIgnore] public string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    [JsonIgnore]
    public string LogDirectory => string.IsNullOrWhiteSpace(Logging.Directory)
        ? Path.Combine(DataDirectory, "logs")
        : Logging.Directory!;

    [JsonIgnore]
    public string FailedSpoolDirectory => string.IsNullOrWhiteSpace(Delivery.FailedSpoolDirectory)
        ? Path.Combine(DataDirectory, "failed")
        : Delivery.FailedSpoolDirectory!;
}
