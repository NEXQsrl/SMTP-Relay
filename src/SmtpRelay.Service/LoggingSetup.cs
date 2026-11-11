using Serilog;
using Serilog.Events;
using Serilog.Filters;
using SmtpRelay.Configuration;

namespace SmtpRelay;

/// <summary>Builds the Serilog logger: rotation, retention, separate audit trail and Windows event log.</summary>
public static class LoggingSetup
{
    /// <summary>ILogger category whose events go to the audit file.</summary>
    public const string AuditSourceContext = "SmtpRelay.Audit";

    /// <summary>Source of the entries written to the Windows Application event log.</summary>
    public const string EventLogSource = "SmtpRelay";

    public static Serilog.Core.Logger CreateLogger(RelayOptions options, bool runningAsService)
    {
        var log = options.Logging;
        var level = ParseLevel(log.MinimumLevel);
        var directory = options.LogDirectory;
        Directory.CreateDirectory(directory);

        TimeSpan? timeLimit = log.RetainedFileTimeLimitDays is int days && days > 0
            ? TimeSpan.FromDays(days)
            : null;

        const string template =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext();

        if (log.Console && !runningAsService)
            config.WriteTo.Console(outputTemplate: template);

        // Warnings and errors also reach the Windows event log, where monitoring tools look first.
        // The source is registered by "SmtpRelay install"; without it the sink stays silent.
        if (log.EventLog && runningAsService)
        {
            config.WriteTo.EventLog(
                source: EventLogSource,
                logName: "Application",
                manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning,
                outputTemplate: "{SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        // Application log (delivery events are left out when the separate audit trail is on).
        config.WriteTo.Logger(sub =>
        {
            if (log.AuditTrail)
                sub.Filter.ByExcluding(Matching.FromSource(AuditSourceContext));

            sub.WriteTo.File(
                path: Path.Combine(directory, "smtp-relay-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: log.RetainedFileCountLimit,
                retainedFileTimeLimit: timeLimit,
                fileSizeLimitBytes: log.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: template);
        });

        // Audit trail (delivery events only).
        if (log.AuditTrail)
        {
            config.WriteTo.Logger(sub =>
            {
                sub.Filter.ByIncludingOnly(Matching.FromSource(AuditSourceContext));
                sub.WriteTo.File(
                    path: Path.Combine(directory, "audit-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: log.RetainedFileCountLimit,
                    retainedFileTimeLimit: timeLimit,
                    fileSizeLimitBytes: log.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Message:lj}{NewLine}");
            });
        }

        return config.CreateLogger();
    }

    private static LogEventLevel ParseLevel(string value)
        => Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;
}
