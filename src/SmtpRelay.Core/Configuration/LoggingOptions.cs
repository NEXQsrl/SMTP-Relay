namespace SmtpRelay.Configuration;

/// <summary>Logging and retention settings.</summary>
public sealed class LoggingOptions
{
    /// <summary>Log folder. Empty = {DataDirectory}\logs.</summary>
    public string? Directory { get; set; }

    /// <summary>Maximum number of log files kept (daily rotation plus rotation by size).</summary>
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>Maximum age of log files in days. Null = no time limit.</summary>
    public int? RetainedFileTimeLimitDays { get; set; } = 31;

    /// <summary>Maximum size of one log file before it is rotated (bytes).</summary>
    public long FileSizeLimitBytes { get; set; } = 52_428_800; // 50 MB

    /// <summary>Minimum level: Verbose, Debug, Information, Warning, Error, Fatal.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Also write to the console (useful when run interactively).</summary>
    public bool Console { get; set; } = true;

    /// <summary>Write delivery events to a separate audit file.</summary>
    public bool AuditTrail { get; set; } = true;

    /// <summary>Write warnings and errors to the Windows Application event log (source "SmtpRelay").</summary>
    public bool EventLog { get; set; } = true;
}
