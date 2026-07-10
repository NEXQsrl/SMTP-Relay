using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SmtpRelay;
using SmtpRelay.Cli;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;
using SmtpRelay.Smtp;

var exeDir = AppContext.BaseDirectory;
var configuration = ConfigLoader.Build(exeDir, args);
var options = ConfigLoader.BindOptions(configuration);

// Crea la cartella dati. I file sensibili (entropy.bin, secrets.dat) vengono protetti
// singolarmente con ACL ristretta a SYSTEM/Administrators; i log restano leggibili dagli amministratori.
try { Directory.CreateDirectory(options.DataDirectory); }
catch { /* la creazione effettiva avviene comunque nei percorsi specifici */ }

// Comandi di amministrazione (install, set-credential, test-upstream, ...).
if (args.Length > 0 && CommandLineHandler.IsVerb(args[0]))
    return await CommandLineHandler.RunAsync(args, options);

// Validazione fail-fast della configurazione.
var validation = new RelayOptionsValidator().Validate(null, options);
if (validation.Failed)
{
    Console.Error.WriteLine("Configurazione non valida:");
    foreach (var failure in validation.Failures ?? Enumerable.Empty<string>())
        Console.Error.WriteLine("  - " + failure);
    return 2;
}

Log.Logger = LoggingSetup.CreateLogger(options);
try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = exeDir
    });

    builder.Logging.ClearProviders();
    builder.Services.AddWindowsService(o => o.ServiceName = options.ServiceName);
    builder.Services.AddSerilog();

    // Opzioni
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(options.Upstream);
    builder.Services.AddSingleton(options.Upstream.Microsoft365);

    // Sicurezza / segreti
    var entropy = MachineKey.LoadOrCreate(options.EntropyFilePath);
    var protector = new SecretProtector(entropy);
    builder.Services.AddSingleton(protector);
    builder.Services.AddSingleton(new SecretStore(options.SecretsFilePath, protector));
    builder.Services.AddSingleton(new IpAllowList(options.Security.AllowedIps, options.Security.RejectWhenAllowListEmpty));

    // Relay in uscita
    builder.Services.AddSingleton<IM365TokenProvider, Microsoft365TokenProvider>();
    builder.Services.AddSingleton<IUpstreamSender, UpstreamSender>();

    // Servizio SMTP in ascolto
    builder.Services.AddHostedService<SmtpRelayHostedService>();

    var host = builder.Build();
    Log.Information("Avvio SMTP Relay (servizio '{Service}').", options.ServiceName);
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Errore fatale durante l'esecuzione del relay.");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
