using Serilog;
using Serilog.Events;
using Serilog.Filters;
using SmtpRelay.Configuration;

namespace SmtpRelay;

/// <summary>Costruisce il logger Serilog con rotazione, retention e audit trail separato.</summary>
public static class LoggingSetup
{
    /// <summary>Categoria ILogger i cui eventi finiscono nel file di audit.</summary>
    public const string AuditSourceContext = "SmtpRelay.Audit";

    public static Serilog.Core.Logger CreateLogger(RelayOptions options)
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

        if (log.Console)
            config.WriteTo.Console(outputTemplate: template);

        // Log applicativo generale (esclude gli eventi di audit se il trail separato e attivo).
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

        // Audit trail dedicato (solo eventi di consegna).
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
