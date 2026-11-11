using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SmtpRelay.Configuration;
using SmtpRelay.Relay;
using SmtpRelay.Security;

namespace SmtpRelay.Cli;

/// <summary>Administration commands of the CLI.</summary>
[SupportedOSPlatform("windows")]
public static class CommandLineHandler
{
    private const string ServiceDisplayName = "NEXQ SMTP Relay";
    private const string ServiceDescription =
        "Accepts mail from allowed internal senders and forwards it to Microsoft 365 or to an SMTP smart host.";

    private static readonly string[] Verbs =
    {
        "help", "--help", "-h", "/?", "version", "--version",
        "install", "uninstall", "start", "stop", "restart", "status",
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
                "version" or "--version" => PrintVersion(),
                "install" => Install(options, map),
                "uninstall" => RequireAdmin() ?? ServiceControl.Uninstall(options.ServiceName),
                "start" => RequireAdmin() ?? ServiceControl.Start(options.ServiceName),
                "stop" => RequireAdmin() ?? ServiceControl.Stop(options.ServiceName),
                "restart" => RequireAdmin() ?? Restart(options.ServiceName),
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
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static int Install(RelayOptions options, Dictionary<string, string> map)
    {
        var admin = RequireAdmin();
        if (admin is not null)
            return admin.Value;

        if (!RelayOptionsValidator.IsValidName(options.ServiceName))
        {
            Console.Error.WriteLine("ServiceName in appsettings.json is not valid.");
            return 2;
        }

        map.TryGetValue("account", out var account);
        map.TryGetValue("password", out var password);
        if (map.ContainsKey("password-stdin"))
            password = Console.In.ReadLine();

        var code = ServiceControl.Install(options.ServiceName, ServiceDisplayName, ServiceDescription, account, password);
        if (code != 0)
            return code;

        // Close the data folder to ordinary users (logs and the failed-message spool contain mail data).
        var serviceAccount = ServiceControl.GetAccount(options.ServiceName);
        try
        {
            WindowsAcl.HardenDataDirectory(options.DataDirectory, serviceAccount);
            if (WindowsAcl.ResolveAccount(serviceAccount) is null &&
                !serviceAccount.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Warning: the account '{serviceAccount}' could not be resolved. " +
                                        $"Give it Modify rights on {options.DataDirectory} yourself.");
            }
            else
            {
                Console.WriteLine($"Data folder protected: {options.DataDirectory}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: cannot restrict the permissions of {options.DataDirectory}: {ex.Message}");
        }

        try
        {
            if (!EventLog.SourceExists(LoggingSetup.EventLogSource))
                EventLog.CreateEventSource(LoggingSetup.EventLogSource, "Application");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: cannot register the event log source: {ex.Message}");
        }

        return 0;
    }

    private static int Restart(string serviceName)
    {
        var code = ServiceControl.Stop(serviceName);
        return code != 0 ? code : ServiceControl.Start(serviceName);
    }

    private static int SetCredential(RelayOptions options, Dictionary<string, string> map)
    {
        if (!map.TryGetValue("name", out var name) || !RelayOptionsValidator.IsValidName(name))
        {
            Console.Error.WriteLine("Usage: SmtpRelay set-credential --name <name> [--stdin | --value <secret>]");
            return 2;
        }

        string value;
        if (map.TryGetValue("value", out var v))
            value = v;
        else if (map.ContainsKey("stdin") || Console.IsInputRedirected)
            value = Console.In.ReadToEnd().TrimEnd('\r', '\n');
        else
            value = ReadMasked($"Enter the value for '{name}': ");

        if (string.IsNullOrEmpty(value))
        {
            Console.Error.WriteLine("Empty value: nothing was saved.");
            return 2;
        }

        SecretStore.Open(options).Set(name, value);
        Console.WriteLine($"Secret '{name}' encrypted and saved in {options.SecretsFilePath}");
        return 0;
    }

    private static int RemoveCredential(RelayOptions options, Dictionary<string, string> map)
    {
        if (!map.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Usage: SmtpRelay remove-credential --name <name>");
            return 2;
        }

        Console.WriteLine(SecretStore.Open(options).Remove(name)
            ? $"Secret '{name}' removed."
            : $"Secret '{name}' not found.");
        return 0;
    }

    private static int ListCredentials(RelayOptions options)
    {
        var names = SecretStore.Open(options).Names;
        if (names.Count == 0)
        {
            Console.WriteLine("No secret stored.");
            return 0;
        }

        Console.WriteLine("Stored secrets (values are never shown):");
        foreach (var n in names)
            Console.WriteLine("  - " + n);
        return 0;
    }

    private static async Task<int> TestUpstreamAsync(RelayOptions options)
    {
        var errors = RelayOptionsValidator.Validate(options);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine("The configuration is not valid:");
            foreach (var error in errors)
                Console.Error.WriteLine("  - " + error);
            return 2;
        }

        var secrets = SecretStore.Open(options);
        var tokenProvider = new Microsoft365TokenProvider(
            options.Upstream.Microsoft365, secrets, NullLogger<Microsoft365TokenProvider>.Instance);
        var sender = new UpstreamSender(
            options.Upstream, secrets, tokenProvider, NullLogger<UpstreamSender>.Instance);

        Console.WriteLine($"Testing {options.Upstream.Host}:{options.Upstream.Port} " +
                          $"(authentication {options.Upstream.Mode}, security {options.Upstream.Security})...");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.Upstream.TimeoutSeconds + 10));
        try
        {
            await sender.VerifyConnectionAsync(cts.Token);
            Console.WriteLine(options.Upstream.Mode == UpstreamAuthMode.Anonymous
                ? "OK: connection succeeded."
                : "OK: connection and authentication succeeded.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.Message);
            if (ex.InnerException is not null)
                Console.Error.WriteLine("Detail: " + ex.InnerException.Message);
            return 1;
        }
    }

    private static int ShowConfig(RelayOptions o)
    {
        var secrets = SecretStore.Open(o);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {AppInfo.ProductName} {AppInfo.Version} - effective configuration ===");
        sb.AppendLine($"ServiceName          : {o.ServiceName}");
        sb.AppendLine($"DataDirectory        : {o.DataDirectory}");
        sb.AppendLine($"Listener             : {o.Listener.IpAddress}:{o.Listener.Port} (server name '{o.Listener.ServerName}')");
        sb.AppendLine($"Limits               : {o.Listener.MaxMessageSizeBytes} bytes per message, {o.Listener.MaxRecipients} recipients, {o.Listener.MaxConcurrentSessions} sessions");
        sb.AppendLine($"Inbound TLS          : {o.Tls.Mode} (minimum {o.Tls.MinimumProtocol})");
        sb.AppendLine($"Allowed IPs          : {(o.Security.AllowedIps.Count == 0 ? "(empty)" : string.Join(", ", o.Security.AllowedIps))}");
        sb.AppendLine($"Upstream             : {o.Upstream.Mode} -> {o.Upstream.Host}:{o.Upstream.Port} ({o.Upstream.Security})");
        switch (o.Upstream.Mode)
        {
            case UpstreamAuthMode.Basic:
                sb.AppendLine($"  Username           : {o.Upstream.Basic.Username}");
                sb.AppendLine($"  Password           : {(secrets.Contains(o.Upstream.Basic.PasswordSecret) ? "[set]" : "[MISSING]")} (secret '{o.Upstream.Basic.PasswordSecret}')");
                break;
            case UpstreamAuthMode.Microsoft365:
                sb.AppendLine($"  TenantId           : {o.Upstream.Microsoft365.TenantId}");
                sb.AppendLine($"  ClientId           : {o.Upstream.Microsoft365.ClientId}");
                sb.AppendLine($"  SenderUsername     : {o.Upstream.Microsoft365.SenderUsername}");
                sb.AppendLine($"  ClientSecret       : {(secrets.Contains(o.Upstream.Microsoft365.ClientSecretName) ? "[set]" : "[MISSING]")} (secret '{o.Upstream.Microsoft365.ClientSecretName}')");
                break;
        }
        sb.AppendLine($"Logs                 : {o.LogDirectory} (keep {o.Logging.RetainedFileCountLimit} files / {o.Logging.RetainedFileTimeLimitDays?.ToString() ?? "unlimited"} days)");
        sb.AppendLine($"Failed-message spool : {(o.Delivery.SpoolFailedMessages ? o.FailedSpoolDirectory : "(off)")}");

        var errors = RelayOptionsValidator.Validate(o);
        var warnings = RelayOptionsValidator.Warnings(o);
        sb.AppendLine();
        if (warnings.Count > 0)
            sb.AppendLine("Warnings             :\n  - " + string.Join("\n  - ", warnings));
        sb.AppendLine(errors.Count == 0
            ? "Validation           : OK"
            : "Validation           : ERRORS\n  - " + string.Join("\n  - ", errors));

        Console.WriteLine(sb.ToString());
        return errors.Count == 0 ? 0 : 2;
    }

    private static int? RequireAdmin()
    {
        if (WindowsAcl.IsAdministrator())
            return null;
        Console.Error.WriteLine("This command needs administrator rights. Open the terminal as administrator.");
        return 5;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
                continue;
            var key = a[2..];
            // The value of --value / --password may itself start with "--".
            bool takesValue = key is "name" or "value" or "account" or "password";
            if (i + 1 < args.Length && (takesValue || !args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                map[key] = args[++i];
            else
                map[key] = "true"; // boolean flag (for example --stdin)
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

    private static int PrintVersion()
    {
        Console.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
$"""
{AppInfo.ProductName} {AppInfo.Version} - Windows SMTP relay with IP allowlist, TLS and Basic / Microsoft 365 authentication.

USAGE:
  SmtpRelay <command> [options]

SERVICE (administrator rights needed):
  install [--account <user> --password-stdin]      Installs or repairs the Windows service (default: LocalSystem)
  uninstall                                        Removes the service
  start | stop | restart | status                  Controls the service

CREDENTIALS (encrypted with DPAPI, bound to this computer):
  set-credential --name <name> [--stdin]           Saves a secret (asks for the value, or reads it from stdin)
  remove-credential --name <name>                  Removes a secret
  list-credentials                                 Lists the names of the stored secrets

DIAGNOSTICS:
  test-upstream        Connects and authenticates to the smart host
  show-config          Shows the effective configuration and validates it
  version              Shows the version

Without a command the executable runs the relay (as a service, or in the console).

EXAMPLES:
  SmtpRelay set-credential --name M365ClientSecret
  SmtpRelay test-upstream
  SmtpRelay install
  SmtpRelay start
""");
        return 0;
    }
}
