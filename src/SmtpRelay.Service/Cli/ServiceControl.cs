using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace SmtpRelay.Cli;

/// <summary>Installs and controls the Windows service (sc.exe for the configuration, the SCM API for the state).</summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    public static bool Exists(string serviceName)
        => ServiceController.GetServices().Any(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Account the installed service runs as ("LocalSystem" when it is not installed).</summary>
    public static string GetAccount(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        return key?.GetValue("ObjectName") as string ?? "LocalSystem";
    }

    public static int Install(string serviceName, string displayName, string description, string? account, string? accountPassword)
    {
        var exePath = GetExecutablePath();
        // The image path is stored quoted: an unquoted path with spaces could be hijacked.
        var quotedExe = "\"" + exePath + "\"";

        int code;
        if (Exists(serviceName))
        {
            // Upgrade or repair: point the existing service to this executable, keep its account and start type.
            code = RunSc("config", serviceName, "binPath=", quotedExe, "DisplayName=", displayName);
            if (code != 0)
                return code;
            Console.WriteLine($"Service '{serviceName}' already installed: updated to {exePath}");
        }
        else
        {
            var arguments = new List<string> { "create", serviceName, "binPath=", quotedExe, "start=", "delayed-auto", "DisplayName=", displayName };
            if (!string.IsNullOrWhiteSpace(account))
            {
                arguments.Add("obj=");
                arguments.Add(account);
                if (!string.IsNullOrEmpty(accountPassword))
                {
                    arguments.Add("password=");
                    arguments.Add(accountPassword);
                }
            }

            code = RunSc(arguments.ToArray());
            if (code != 0)
                return code;
            Console.WriteLine($"Service '{serviceName}' installed. Executable: {exePath}");
        }

        RunSc("description", serviceName, description);
        // Restart automatically after a crash, and also when the service stops with an error.
        RunSc("failure", serviceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
        RunSc("failureflag", serviceName, "1");
        return 0;
    }

    public static int Uninstall(string serviceName)
    {
        if (!Exists(serviceName))
        {
            Console.WriteLine($"Service '{serviceName}' is not installed.");
            return 0;
        }

        Stop(serviceName);
        var code = RunSc("delete", serviceName);
        if (code == 0)
            Console.WriteLine($"Service '{serviceName}' removed.");
        return code;
    }

    public static int Start(string serviceName)
    {
        if (!Exists(serviceName))
            return NotInstalled(serviceName);

        using var controller = new ServiceController(serviceName);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            if (controller.Status != ServiceControllerStatus.StartPending)
                controller.Start();
            try
            {
                controller.WaitForStatus(ServiceControllerStatus.Running, StateTimeout);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                Console.Error.WriteLine($"Service '{serviceName}' did not reach the Running state. Look at the application log for the reason.");
                return 1;
            }
        }

        // A service that refuses its configuration stops right after it started.
        Thread.Sleep(1500);
        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Running)
        {
            Console.Error.WriteLine($"Service '{serviceName}' stopped right after starting. Run 'SmtpRelay show-config' and look at the application log.");
            return 1;
        }

        Console.WriteLine($"Service '{serviceName}' is running.");
        return 0;
    }

    public static int Stop(string serviceName)
    {
        if (!Exists(serviceName))
            return NotInstalled(serviceName);

        using var controller = new ServiceController(serviceName);
        if (controller.Status != ServiceControllerStatus.Stopped)
        {
            if (controller.Status != ServiceControllerStatus.StopPending)
                controller.Stop();
            try
            {
                controller.WaitForStatus(ServiceControllerStatus.Stopped, StateTimeout);
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                Console.Error.WriteLine($"Service '{serviceName}' did not stop within {StateTimeout.TotalSeconds:0} seconds.");
                return 1;
            }
        }

        Console.WriteLine($"Service '{serviceName}' is stopped.");
        return 0;
    }

    public static int Status(string serviceName)
    {
        if (!Exists(serviceName))
            return NotInstalled(serviceName);

        using var controller = new ServiceController(serviceName);
        Console.WriteLine($"Service '{serviceName}': {controller.Status} (start type {controller.StartType}, account {GetAccount(serviceName)})");
        return controller.Status == ServiceControllerStatus.Running ? 0 : 3;
    }

    private static int NotInstalled(string serviceName)
    {
        Console.Error.WriteLine($"Service '{serviceName}' is not installed. Run 'SmtpRelay install' first.");
        return 4; // exit codes above 255 are truncated by the shell
    }

    private static string GetExecutablePath()
    {
        var main = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(main) && main.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(main).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return main;

        return Path.Combine(AppContext.BaseDirectory, "SmtpRelay.exe");
    }

    private static int RunSc(params string[] arguments)
    {
        // Full path: never let the search path decide which sc.exe runs elevated.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // ArgumentList quotes every value: names, accounts and passwords cannot inject further options.
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
                Console.Error.WriteLine(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr))
                Console.Error.WriteLine(stderr.Trim());
        }

        return process.ExitCode;
    }
}
