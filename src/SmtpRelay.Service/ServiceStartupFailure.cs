using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;

namespace SmtpRelay;

/// <summary>
/// Reports why the service cannot start. Windows shows "error 1053: the service did not respond to the start
/// or control request" for a service process that ends before it has answered the service control manager,
/// and the administrator is left without a clue. Here the process answers first, records the reason where the
/// administrator looks (application log, Windows event log) and then stops in an orderly way.
/// </summary>
public static class ServiceStartupFailure
{
    /// <summary>Writes the reason to the application log and, for a service, to the Windows event log.</summary>
    public static void WriteToLog(RelayOptions options, bool runningAsService, string reason)
    {
        try
        {
            using var log = LoggingSetup.CreateLogger(options, runningAsService);
            log.ForContext("SourceContext", "SmtpRelay.Startup").Fatal("The service cannot start. {Reason}", reason);
        }
        catch (Exception)
        {
            // The log folder itself may be what is wrong: fall back to the event log alone.
            if (!runningAsService)
                return;
            try
            {
                EventLog.WriteEntry(LoggingSetup.EventLogSource, "The service cannot start. " + reason, EventLogEntryType.Error);
            }
            catch (Exception) { /* the source is registered by "SmtpRelay install"; nothing else can be done */ }
        }
    }

    /// <summary>Answers the service control manager, records the reason and stops the service.</summary>
    public static async Task ReportAsync(RelayOptions options, string reason, bool alreadyLogged = false)
    {
        if (!alreadyLogged)
            WriteToLog(options, runningAsService: true, reason);

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddWindowsService(o => o.ServiceName = options.ServiceName);
            builder.Services.AddHostedService<StopAtOnce>();
            using var host = builder.Build();
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing more to report: the reason is already in the log.
        }
    }

    private sealed class StopAtOnce(IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Long enough for the Services console to see the service running before it stops.
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            lifetime.StopApplication();
        }
    }
}
