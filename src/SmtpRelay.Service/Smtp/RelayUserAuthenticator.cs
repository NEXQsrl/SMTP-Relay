using System.Net;
using Microsoft.Extensions.Logging;
using SmtpRelay.Security;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Net;

namespace SmtpRelay.Smtp;

/// <summary>Values the relay keeps in the properties of an SMTP session.</summary>
public static class SessionKeys
{
    /// <summary>Set when the client connected from outside the allowlist: it can send only after signing in.</summary>
    public const string MustAuthenticate = "SmtpRelay:MustAuthenticate";

    /// <summary>Name of the relay user that signed in (as registered, not as typed by the client).</summary>
    public const string User = "SmtpRelay:User";

    public static string? GetUser(ISessionContext context)
        => context.Properties.TryGetValue(User, out var value) ? value as string : null;

    public static IPAddress? GetRemoteAddress(ISessionContext context)
        => context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) && value is IPEndPoint endPoint
            ? endPoint.Address
            : null;
}

/// <summary>
/// Checks the user name and the password of SMTP AUTH against the users of the relay. An address that keeps
/// failing is refused for a while without looking at the password, and every failure is answered slowly.
/// </summary>
public sealed class RelayUserAuthenticator : UserAuthenticator
{
    private readonly RelayUserStore _users;
    private readonly FailedSignInThrottle _throttle;
    private readonly TimeSpan _failureDelay;
    private readonly ILogger _logger;
    private readonly ThrottledLog _failureLog = new(TimeSpan.FromMinutes(1));

    public RelayUserAuthenticator(RelayUserStore users, ILogger logger, FailedSignInThrottle? throttle = null, TimeSpan? failureDelay = null)
    {
        _users = users;
        _logger = logger;
        _throttle = throttle ?? new FailedSignInThrottle(maxFailures: 10, window: TimeSpan.FromMinutes(10));
        _failureDelay = failureDelay ?? TimeSpan.FromSeconds(1);
    }

    public override async Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
    {
        var address = SessionKeys.GetRemoteAddress(context)?.ToString() ?? "unknown";
        var typedName = LogSanitizer.Clean(user ?? "", 64);

        if (_throttle.IsBlocked(address))
        {
            if (_failureLog.ShouldLog("blocked:" + address, out var suppressed))
            {
                _logger.LogWarning(
                    "Sign-in from {RemoteIp} REFUSED without checking the password: too many failed attempts from this address ({Suppressed} more refused since the last entry).",
                    address, suppressed);
            }
            await Task.Delay(_failureDelay, cancellationToken).ConfigureAwait(false);
            return false;
        }

        string? name;
        try
        {
            name = _users.Verify(user, password);
        }
        catch (Exception ex)
        {
            // Fail closed: when the users cannot be read nobody signs in.
            _logger.LogError(ex, "The users of the relay cannot be read: the sign-in of '{User}' from {RemoteIp} is refused.", typedName, address);
            name = null;
        }

        if (name is not null)
        {
            _throttle.Reset(address);
            context.Properties[SessionKeys.User] = name;
            _logger.LogInformation("User '{User}' signed in from {RemoteIp}.", name, address);
            return true;
        }

        _throttle.RecordFailure(address);
        if (_failureLog.ShouldLog(address + "|" + typedName, out var more))
        {
            _logger.LogWarning(
                "Sign-in FAILED for user '{User}' from {RemoteIp}: unknown or disabled user, or wrong password ({Suppressed} more failures since the last entry).",
                typedName, address, more);
        }
        await Task.Delay(_failureDelay, cancellationToken).ConfigureAwait(false);
        return false;
    }
}

/// <summary>Counts the failed sign-ins of each address and blocks the address when they are too many in a period.</summary>
public sealed class FailedSignInThrottle
{
    private const int MaxKeys = 4096;
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, (int Count, DateTime Start)> _state = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public FailedSignInThrottle(int maxFailures, TimeSpan window)
    {
        _maxFailures = maxFailures;
        _window = window;
    }

    public bool IsBlocked(string key)
    {
        lock (_sync)
        {
            if (!_state.TryGetValue(key, out var entry))
                return false;
            if (DateTime.UtcNow - entry.Start >= _window)
            {
                _state.Remove(key);
                return false;
            }
            return entry.Count >= _maxFailures;
        }
    }

    public void RecordFailure(string key)
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (_state.TryGetValue(key, out var entry) && now - entry.Start < _window)
            {
                _state[key] = (entry.Count + 1, entry.Start);
                return;
            }

            if (_state.Count >= MaxKeys)
            {
                // Keep the addresses that are blocked now; forget the ones that only failed a few times.
                foreach (var stale in _state.Where(p => p.Value.Count < _maxFailures || now - p.Value.Start >= _window).Select(p => p.Key).ToList())
                    _state.Remove(stale);
                if (_state.Count >= MaxKeys)
                    _state.Clear();
            }
            _state[key] = (1, now);
        }
    }

    public void Reset(string key)
    {
        lock (_sync) { _state.Remove(key); }
    }
}
