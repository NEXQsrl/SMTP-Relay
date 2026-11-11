using System.IO;
using System.Security.Principal;

namespace SmtpRelay.Manager.Services;

/// <summary>
/// Records what administrators do in the console (settings saved, secrets changed, service controlled, updates)
/// in manager-yyyyMM.log, next to the service logs.
/// </summary>
public static class ManagerLog
{
    private static readonly object Sync = new();
    private static string? _directory;
    private static readonly string User = ReadUser();

    public static void Initialize(string logDirectory) => _directory = logDirectory;

    public static void Write(string message)
    {
        if (_directory is null)
            return;

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"manager-{DateTime.Now:yyyyMM}.log");
                File.AppendAllText(path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{User}] {message.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string ReadUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch (Exception)
        {
            return Environment.UserName;
        }
    }
}
