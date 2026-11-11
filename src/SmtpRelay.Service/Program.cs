using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using SmtpRelay;
using SmtpRelay.Cli;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpRelay.Smtp;

var exeDir = AppContext.BaseDirectory;

RelayOptions options;
try
{
    options = ConfigLoader.Load(exeDir);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot read {ConfigLoader.FileName} in {exeDir}: {ex.Message}");
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
bool runningAsService = WindowsServiceHelpers.IsWindowsService();

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

    // A service has no console: leave the reason where the administrator will look for it.
    try
    {
        using var startupLog = LoggingSetup.CreateLogger(options, runningAsService);
        startupLog.ForContext("SourceContext", "SmtpRelay.Startup")
            .Fatal("The service cannot start, the configuration is not valid: {Errors}", string.Join(" | ", errors));
    }
    catch (Exception) { /* the log folder itself may be what is wrong */ }
    return 2;
}

Log.Logger = LoggingSetup.CreateLogger(options, runningAsService);
try
{
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
    builder.Services.AddSingleton(options.Upstream.Microsoft365);

    // Security and secrets
    builder.Services.AddSingleton(SecretStore.Open(options));
    builder.Services.AddSingleton(new IpAllowList(options.Security.AllowedIps, options.Security.RejectWhenAllowListEmpty));

    // Outbound relay
    builder.Services.AddSingleton<IM365TokenProvider, Microsoft365TokenProvider>();
    builder.Services.AddSingleton<IUpstreamSender, UpstreamSender>();

    // Listening SMTP server
    builder.Services.AddHostedService<SmtpRelayHostedService>();

    var host = builder.Build();
    Log.Information("NEXQ SMTP Relay {Version} starting (service '{Service}').", AppInfo.Version, options.ServiceName);
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fatal error while running the relay.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
