using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using SmtpRelay;
using SmtpRelay.Cli;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpRelay.Smtp;
using SmtpRelay.Snmp;

var exeDir = AppContext.BaseDirectory;
bool runningAsService = WindowsServiceHelpers.IsWindowsService();

RelayOptions options;
try
{
    options = ConfigLoader.Load(exeDir);
}
catch (Exception ex)
{
    var reason = $"Cannot read {ConfigLoader.FileName} in {exeDir}: {ex.Message}";
    Console.Error.WriteLine(reason);
    if (runningAsService)
        await ServiceStartupFailure.ReportAsync(new RelayOptions(), reason);
    return 2;
}

// Administration commands (install, set-credential, test-upstream, ...).
if (args.Length > 0 && CommandLineHandler.IsVerb(args[0]))
    return await CommandLineHandler.RunAsync(args, options);

if (args.Length > 0)
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'. Run 'SmtpRelay help' for the list of commands.");
    return 2;
}

// Fail fast on an invalid configuration.
var errors = RelayOptionsValidator.Validate(options);

try
{
    // As LocalSystem the data folder can be closed to ordinary users before anything is written to it.
    // With a custom service account this is done once by "SmtpRelay install".
    using (var identity = WindowsIdentity.GetCurrent())
    {
        if (identity.IsSystem && errors.Count == 0)
            WindowsAcl.HardenDataDirectory(options.DataDirectory);
        else
            Directory.CreateDirectory(options.DataDirectory);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot prepare the data folder '{options.DataDirectory}': {ex.Message}");
}

if (errors.Count > 0)
{
    Console.Error.WriteLine("The configuration is not valid:");
    foreach (var error in errors)
        Console.Error.WriteLine("  - " + error);

    // A service that exits before it has answered the service control manager is reported by Windows as
    // "error 1053: the service did not respond", which says nothing. Answer first, then stop with the reason
    // in the application log and in the Windows event log.
    var reason = "The configuration is not valid (open the SMTP Relay console, or run 'SmtpRelay show-config'): " +
                 string.Join(" | ", errors);
    if (runningAsService)
        await ServiceStartupFailure.ReportAsync(options, reason);
    else
        ServiceStartupFailure.WriteToLog(options, runningAsService, reason);
    return 2;
}

bool hostStarted = false;
try
{
    Log.Logger = LoggingSetup.CreateLogger(options, runningAsService);

    foreach (var warning in RelayOptionsValidator.Warnings(options))
        Log.Warning("Configuration: {Warning}", warning);

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = exeDir
    });

    builder.Logging.ClearProviders();
    builder.Services.AddWindowsService(o => o.ServiceName = options.ServiceName);
    builder.Services.AddSerilog();

    // Options
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(options.Upstream);

    // Security and secrets
    builder.Services.AddSingleton(SecretStore.Open(options));
    builder.Services.AddSingleton(new IpAllowList(options.Security.AllowedIps, options.Security.RejectWhenAllowListEmpty));

    // Outbound relay
    builder.Services.AddSingleton<IAccessTokenProvider, EntraTokenProvider>();
    builder.Services.AddSingleton<IUpstreamSender, UpstreamSender>();

    // Listening SMTP server
    builder.Services.AddSingleton(DeliveryCounters.FromAuditTrail(options.LogDirectory));
    builder.Services.AddHostedService<SmtpRelayHostedService>();

    // Monitoring
    if (options.Snmp.Enabled)
        builder.Services.AddHostedService<SnmpAgentService>();

    var host = builder.Build();
    Log.Information("NEXQ SMTP Relay {Version} starting (service '{Service}').", AppInfo.Version, options.ServiceName);
    hostStarted = true;
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal error while running the relay.");
    Console.Error.WriteLine("Fatal error: " + ex.Message);

    // Same as above: a failure before the host runs (secret store, log folder...) must not look like error 1053.
    if (runningAsService && !hostStarted)
    {
        Log.CloseAndFlush();
        await ServiceStartupFailure.ReportAsync(options, "The relay could not start: " + ex.Message, alreadyLogged: true);
    }
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
