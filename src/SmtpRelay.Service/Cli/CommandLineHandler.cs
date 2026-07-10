using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;

namespace SmtpRelay.Cli;

/// <summary>Gestisce i comandi di amministrazione della CLI.</summary>
[SupportedOSPlatform("windows")]
public static class CommandLineHandler
{
    private static readonly string[] Verbs =
    {
        "help", "--help", "-h", "/?",
        "install", "uninstall", "start", "stop", "status",
        "set-credential", "remove-credential", "list-credentials",
        "test-upstream", "show-config"
    };

    public static bool IsVerb(string arg) => Verbs.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, RelayOptions options)
    {
        var verb = args[0].ToLowerInvariant();
        var map = ParseArgs(args);

        try
        {
            return verb switch
            {
                "help" or "--help" or "-h" or "/?" => PrintHelp(),
                "install" => Install(options, map),
                "uninstall" => RequireAdmin() ?? ServiceControl.Uninstall(options.ServiceName),
                "start" => RequireAdmin() ?? ServiceControl.Start(options.ServiceName),
                "stop" => RequireAdmin() ?? ServiceControl.Stop(options.ServiceName),
                "status" => ServiceControl.Status(options.ServiceName),
                "set-credential" => SetCredential(options, map),
                "remove-credential" => RemoveCredential(options, map),
                "list-credentials" => ListCredentials(options),
                "test-upstream" => await TestUpstreamAsync(options),
                "show-config" => ShowConfig(options),
                _ => PrintHelp()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Errore: " + ex.Message);
            return 1;
        }
    }

    private static int Install(RelayOptions options, Dictionary<string, string> map)
    {
        var admin = RequireAdmin();
        if (admin is not null)
            return admin.Value;

        map.TryGetValue("account", out var account);
        map.TryGetValue("password", out var password);
        return ServiceControl.Install(
            options.ServiceName,
            displayName: "SMTP Relay Service",
            description: "SMTP Relay aziendale con allowlist IP, TLS e inoltro autenticato (Basic/Microsoft 365).",
            account: account,
            accountPassword: password);
    }

    private static int SetCredential(RelayOptions options, Dictionary<string, string> map)
    {
        if (!map.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Uso: SmtpRelay set-credential --name <nome> [--value <segreto> | --stdin]");
            return 2;
        }

        string value;
        if (map.TryGetValue("value", out var v))
            value = v;
        else if (map.ContainsKey("stdin"))
            value = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        else
            value = ReadMasked($"Inserisci il valore per '{name}': ");

        if (string.IsNullOrEmpty(value))
        {
            Console.Error.WriteLine("Valore vuoto: operazione annullata.");
            return 2;
        }

        var secrets = BuildSecretStore(options);
        secrets.Set(name, value);
        Console.WriteLine($"Segreto '{name}' cifrato e salvato in {options.SecretsFilePath}");
        return 0;
    }

    private static int RemoveCredential(RelayOptions options, Dictionary<string, string> map)
    {
        if (!map.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Uso: SmtpRelay remove-credential --name <nome>");
            return 2;
        }

        var secrets = BuildSecretStore(options);
        Console.WriteLine(secrets.Remove(name)
            ? $"Segreto '{name}' rimosso."
            : $"Segreto '{name}' non trovato.");
        return 0;
    }

    private static int ListCredentials(RelayOptions options)
    {
        var secrets = BuildSecretStore(options);
        var names = secrets.Names;
        if (names.Count == 0)
        {
            Console.WriteLine("Nessun segreto salvato.");
            return 0;
        }

        Console.WriteLine("Segreti salvati (i valori non vengono mostrati):");
        foreach (var n in names.OrderBy(x => x))
            Console.WriteLine("  - " + n);
        return 0;
    }

    private static async Task<int> TestUpstreamAsync(RelayOptions options)
    {
        var secrets = BuildSecretStore(options);
        var tokenProvider = new Microsoft365TokenProvider(
            options.Upstream.Microsoft365, secrets, NullLogger<Microsoft365TokenProvider>.Instance);
        var sender = new UpstreamSender(
            options.Upstream, secrets, tokenProvider, NullLogger<UpstreamSender>.Instance);

        Console.WriteLine($"Test connessione verso {options.Upstream.Host}:{options.Upstream.Port} " +
                          $"(modalita {options.Upstream.Mode}, sicurezza {options.Upstream.Security})...");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Upstream.TimeoutSeconds + 10));
        try
        {
            await sender.VerifyConnectionAsync(cts.Token);
            Console.WriteLine("OK: connessione e autenticazione riuscite.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FALLITO: " + ex.Message);
            if (ex.InnerException is not null)
                Console.Error.WriteLine("Dettaglio: " + ex.InnerException.Message);
            return 1;
        }
    }

    private static int ShowConfig(RelayOptions o)
    {
        var secrets = BuildSecretStore(o);
        var sb = new StringBuilder();
        sb.AppendLine("=== Configurazione effettiva ===");
        sb.AppendLine($"ServiceName          : {o.ServiceName}");
        sb.AppendLine($"DataDirectory        : {o.DataDirectory}");
        sb.AppendLine($"Listener             : {o.Listener.IpAddress}:{o.Listener.Port} (server '{o.Listener.ServerName}')");
        sb.AppendLine($"MaxMessageSize       : {o.Listener.MaxMessageSizeBytes} byte, MaxRecipients={o.Listener.MaxRecipients}");
        sb.AppendLine($"TLS ingresso         : {o.Tls.Mode} (min {o.Tls.MinimumProtocol})");
        sb.AppendLine($"IP consentiti        : {(o.Security.AllowedIps.Count == 0 ? "(vuoto)" : string.Join(", ", o.Security.AllowedIps))}");
        sb.AppendLine($"Upstream             : {o.Upstream.Mode} -> {o.Upstream.Host}:{o.Upstream.Port} ({o.Upstream.Security})");
        if (o.Upstream.Mode == UpstreamAuthMode.Basic)
        {
            sb.AppendLine($"  Username           : {o.Upstream.Basic.Username}");
            sb.AppendLine($"  Password           : {(secrets.Contains(o.Upstream.Basic.PasswordSecret) ? "[impostata]" : "[MANCANTE]")} (segreto '{o.Upstream.Basic.PasswordSecret}')");
        }
        else
        {
            sb.AppendLine($"  TenantId           : {o.Upstream.Microsoft365.TenantId}");
            sb.AppendLine($"  ClientId           : {o.Upstream.Microsoft365.ClientId}");
            sb.AppendLine($"  SenderUsername     : {o.Upstream.Microsoft365.SenderUsername}");
            sb.AppendLine($"  ClientSecret       : {(secrets.Contains(o.Upstream.Microsoft365.ClientSecretName) ? "[impostato]" : "[MANCANTE]")} (segreto '{o.Upstream.Microsoft365.ClientSecretName}')");
        }
        sb.AppendLine($"Log                  : {o.LogDirectory} (retention {o.Logging.RetainedFileCountLimit} file / {o.Logging.RetainedFileTimeLimitDays} giorni)");
        sb.AppendLine($"Spool falliti        : {o.FailedSpoolDirectory}");

        var validation = new RelayOptionsValidator().Validate(null, o);
        sb.AppendLine();
        sb.AppendLine(validation.Succeeded
            ? "Validazione          : OK"
            : "Validazione          : ERRORI\n  - " + string.Join("\n  - ", validation.Failures ?? Array.Empty<string>()));

        Console.WriteLine(sb.ToString());
        return validation.Succeeded ? 0 : 2;
    }

    private static SecretStore BuildSecretStore(RelayOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var entropy = MachineKey.LoadOrCreate(options.EntropyFilePath);
        var protector = new SecretProtector(entropy);
        return new SecretStore(options.SecretsFilePath, protector);
    }

    private static int? RequireAdmin()
    {
        if (ServiceControl.IsAdministrator())
            return null;
        Console.Error.WriteLine("Questo comando richiede privilegi di amministratore. Esegui il terminale come amministratore.");
        return 5;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--"))
                continue;
            var key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                map[key] = args[++i];
            }
            else
            {
                map[key] = "true"; // flag booleano (es. --stdin)
            }
        }
        return map;
    }

    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
"""
SMTP Relay - servizio Windows di inoltro SMTP con allowlist IP, TLS e auth Basic/Microsoft 365.

USO:
  SmtpRelay <comando> [opzioni]

COMANDI SERVIZIO (richiedono amministratore):
  install [--account <utente> --password <pwd>]   Installa il servizio Windows (default: LocalSystem)
  uninstall                                        Rimuove il servizio
  start | stop | status                            Avvia / ferma / stato del servizio

CREDENZIALI (cifrate con DPAPI, per-macchina):
  set-credential --name <nome> [--value <val> | --stdin]   Salva un segreto cifrato
  remove-credential --name <nome>                          Rimuove un segreto
  list-credentials                                         Elenca i nomi dei segreti

DIAGNOSTICA:
  test-upstream        Verifica connessione e autenticazione verso l'upstream
  show-config          Mostra la configurazione effettiva e la valida

Senza comando l'eseguibile parte come servizio/console (host).

ESEMPI:
  SmtpRelay set-credential --name M365ClientSecret --value "abc123~secret"
  SmtpRelay test-upstream
  SmtpRelay install
""");
        return 0;
    }
}
