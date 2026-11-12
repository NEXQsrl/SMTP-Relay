using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace SmtpRelay.Manager.Services;

public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping, Other }

/// <summary>Reads and changes the state of the Windows service.</summary>
public sealed class ServiceManager
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ServiceState GetState(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending => ServiceState.Starting,
                ServiceControllerStatus.StopPending => ServiceState.Stopping,
                _ => ServiceState.Other
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }

    public void Start(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Running)
            return;
        if (controller.Status != ServiceControllerStatus.StartPending)
            controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);

        // A service that refuses its configuration stops right after it started.
        Thread.Sleep(1500);
        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Running)
            throw new InvalidOperationException("The service stopped right after starting: it refused its settings, or could not open the port. " +
                "The Overview page lists what is wrong with the settings; the Logs page has the exact reason.");
    }

    public void Stop(string serviceName)
    {
        using var controller = new ServiceController(serviceName);
        if (controller.Status == ServiceControllerStatus.Stopped)
            return;
        if (controller.Status != ServiceControllerStatus.StopPending)
            controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    public void Restart(string serviceName)
    {
        Stop(serviceName);
        Start(serviceName);
    }

    /// <summary>Runs a command of SmtpRelay.exe (install, uninstall...) and returns its exit code and output.</summary>
    public static (int ExitCode, string Output) RunCli(params string[] arguments)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "SmtpRelay.exe");
        if (!File.Exists(exe))
            return (-1, $"SmtpRelay.exe was not found in {AppContext.BaseDirectory}.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, (stdout + stderr.GetAwaiter().GetResult()).Trim());
    }
}
