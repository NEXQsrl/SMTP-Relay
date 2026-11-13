using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SmtpRelay.Manager.Services;

/// <summary>The administrator whose console can change the settings.</summary>
public sealed record ConsoleHolder(string User, DateTime SinceUtc);

/// <summary>A request to close the console, left by an administrator who has it open read-only.</summary>
public sealed record CloseRequest(string User, DateTime TimeUtc);

/// <summary>
/// Lets one console at a time change the settings, on a server where several administrators are signed in.
/// The first console holds a machine-wide mutex and is the editor; the others open read-only, see who the
/// editor is, and can ask it to close. The mutex is released by Windows when the editor ends, however it ends.
/// Who holds it and the requests are small files in the data folder, which administrators can read and write.
/// </summary>
public sealed class ConsoleLock : IDisposable
{
    private const string MutexName = @"Global\NEXQ.SmtpRelay.Manager";

    private readonly string _holderPath;
    private readonly string _requestPath;
    private readonly string _replyPath;
    private readonly string _mutexName;
    private Mutex? _mutex;

    public ConsoleLock(string dataDirectory, string? mutexName = null)
    {
        _holderPath = Path.Combine(dataDirectory, "console-lock.json");
        _requestPath = Path.Combine(dataDirectory, "console-request.json");
        _replyPath = Path.Combine(dataDirectory, "console-reply.json");
        _mutexName = mutexName ?? MutexName;
    }

    public static string CurrentUser => $@"{Environment.UserDomainName}\{Environment.UserName}";

    /// <summary>True while this console is the one that can change the settings.</summary>
    public bool IsOwner { get; private set; }

    /// <summary>
    /// Tries to become the editor. Must always be called from the same thread (the UI thread), which is also
    /// the one that releases it.
    /// </summary>
    public bool TryAcquire()
    {
        if (IsOwner)
            return true;

        try
        {
            _mutex ??= OpenMutex();
            bool acquired;
            try { acquired = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; } // the editor ended without closing
            if (!acquired)
                return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Held by a console that does not share it (an older version, or another account): read-only.
            _mutex?.Dispose();
            _mutex = null;
            return false;
        }

        IsOwner = true;
        Write(_holderPath, new ConsoleHolder(CurrentUser, DateTime.UtcNow));
        Delete(_requestPath);
        Delete(_replyPath);
        return true;
    }

    /// <summary>Stops being the editor, so that another console can take over.</summary>
    public void Release()
    {
        if (!IsOwner)
            return;
        IsOwner = false;
        Delete(_holderPath);
        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned by this thread: Windows releases it when the process ends */ }
    }

    /// <summary>Who the editor is, when known.</summary>
    public ConsoleHolder? ReadHolder() => Read<ConsoleHolder>(_holderPath);

    /// <summary>Read-only console: asks the editor to close.</summary>
    public void RequestClose()
    {
        Delete(_replyPath);
        Write(_requestPath, new CloseRequest(CurrentUser, DateTime.UtcNow));
    }

    /// <summary>Editor: the request of another administrator, returned once. Old requests are dropped.</summary>
    public CloseRequest? TakeRequest()
    {
        var request = Read<CloseRequest>(_requestPath);
        if (request is null)
            return null;
        Delete(_requestPath);
        return DateTime.UtcNow - request.TimeUtc < TimeSpan.FromMinutes(10) ? request : null;
    }

    /// <summary>Editor: tells the administrator who asked that the console stays open for now.</summary>
    public void Decline() => Write(_replyPath, new ConsoleHolder(CurrentUser, DateTime.UtcNow));

    /// <summary>Read-only console: the name of the editor who declined the request, returned once.</summary>
    public string? TakeDeclined()
    {
        var reply = Read<ConsoleHolder>(_replyPath);
        if (reply is null)
            return null;
        Delete(_replyPath);
        return reply.User;
    }

    public void Dispose()
    {
        Release();
        _mutex?.Dispose();
        _mutex = null;
    }

    // Administrators of other sessions must be able to open the mutex: the default permissions would give it
    // to the account that created it only.
    private Mutex OpenMutex()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is not null)
                security.AddAccessRule(new MutexAccessRule(identity.User, MutexRights.FullControl, AccessControlType.Allow));
        }
        return MutexAcl.Create(initiallyOwned: false, _mutexName, out _, security);
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lock itself is the mutex; these files only carry names and requests.
        }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
