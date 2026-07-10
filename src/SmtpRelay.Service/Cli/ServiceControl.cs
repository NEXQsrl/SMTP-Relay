using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SmtpRelay.Cli;

/// <summary>Wrapper su sc.exe per installare/gestire il servizio Windows.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int Install(string serviceName, string displayName, string description, string? account, string? accountPassword)
    {
        var exePath = GetExecutablePath();
        // Il valore di binPath deve essere il percorso dell'eseguibile racchiuso tra virgolette.
        var quotedExe = "\"" + exePath + "\"";

        var arguments = $"create \"{serviceName}\" binPath= \"{quotedExe}\" start= auto DisplayName= \"{displayName}\"";
        if (!string.IsNullOrWhiteSpace(account))
        {
            arguments += $" obj= \"{account}\"";
            if (!string.IsNullOrWhiteSpace(accountPassword))
                arguments += $" password= \"{accountPassword}\"";
        }

        var code = RunSc(arguments);
        if (code != 0)
            return code;

        RunSc($"description \"{serviceName}\" \"{description}\"");
        // Riavvia automaticamente in caso di crash.
        RunSc($"failure \"{serviceName}\" reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Console.WriteLine($"Servizio '{serviceName}' installato. Eseguibile: {exePath}");
        return 0;
    }

    public static int Uninstall(string serviceName)
    {
        RunSc($"stop \"{serviceName}\"");
        var code = RunSc($"delete \"{serviceName}\"");
        if (code == 0)
            Console.WriteLine($"Servizio '{serviceName}' rimosso.");
        return code;
    }

    public static int Start(string serviceName) => RunSc($"start \"{serviceName}\"");
    public static int Stop(string serviceName) => RunSc($"stop \"{serviceName}\"");
    public static int Status(string serviceName) => RunSc($"query \"{serviceName}\"");

    private static string GetExecutablePath()
    {
        // In esecuzione single-file/host nativo, il percorso e il processo host (.exe).
        var main = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(main) && main.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return main;

        // Fallback: eseguibile accanto alla dll.
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "SmtpRelay.exe");
        return File.Exists(candidate) ? candidate : (main ?? candidate);
    }

    private static int RunSc(string arguments)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout))
            Console.WriteLine(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr.Trim());

        return process.ExitCode;
    }
}
