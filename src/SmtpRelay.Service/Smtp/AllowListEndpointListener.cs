using System.Net;
using Microsoft.Extensions.Logging;
using SmtpRelay.Security;
using SmtpServer;
using SmtpServer.IO;
using SmtpServer.Net;

namespace SmtpRelay.Smtp;

/// <summary>
/// Wraps the default listener factory to apply the IP allowlist and the session limit at socket level:
/// refused connections are closed at once, before any SMTP banner is sent.
/// </summary>
public sealed class AllowListEndpointListenerFactory : IEndpointListenerFactory
{
    private readonly IEndpointListenerFactory _inner;
    private readonly IpAllowList _allowList;
    private readonly SessionLimiter _limiter;
    private readonly ILogger _logger;
    private readonly bool _acceptAnyAddress;

    /// <param name="acceptAnyAddress">
    /// True when users who sign in may connect from any address: a connection from outside the allowlist is
    /// then accepted, and marked so that it cannot send before signing in.
    /// </param>
    public AllowListEndpointListenerFactory(IEndpointListenerFactory inner, IpAllowList allowList, SessionLimiter limiter, ILogger logger, bool acceptAnyAddress = false)
    {
        _inner = inner;
        _allowList = allowList;
        _limiter = limiter;
        _logger = logger;
        _acceptAnyAddress = acceptAnyAddress;
    }

    public IEndpointListener CreateListener(IEndpointDefinition endpointDefinition)
        => new AllowListEndpointListener(_inner.CreateListener(endpointDefinition), _allowList, _limiter, _logger, _acceptAnyAddress);
}

/// <summary>
/// Accepts connections through the inner listener and drops the ones that come from an address outside the
/// allowlist, or that exceed the session limit, then keeps accepting in the same loop.
/// </summary>
public sealed class AllowListEndpointListener : IEndpointListener
{
    private readonly IEndpointListener _inner;
    private readonly IpAllowList _allowList;
    private readonly SessionLimiter _limiter;
    private readonly ILogger _logger;
    private readonly bool _acceptAnyAddress;
    private readonly ThrottledLog _refusedLog = new(TimeSpan.FromMinutes(1));

    public AllowListEndpointListener(IEndpointListener inner, IpAllowList allowList, SessionLimiter limiter, ILogger logger, bool acceptAnyAddress = false)
    {
        _inner = inner;
        _allowList = allowList;
        _limiter = limiter;
        _logger = logger;
        _acceptAnyAddress = acceptAnyAddress;
    }

    public async Task<ISecurableDuplexPipe> GetPipeAsync(ISessionContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pipe = await _inner.GetPipeAsync(context, cancellationToken).ConfigureAwait(false);
            var remote = GetRemoteEndPoint(context);

            // Fail closed: a connection whose address is unknown is refused like one that is not allowed.
            bool listed = remote is not null && _allowList.IsAllowed(remote.Address);
            if (remote is null || (!listed && !_acceptAnyAddress))
            {
                var key = remote?.Address.ToString() ?? "unknown";
                if (_refusedLog.ShouldLog(key, out var suppressed))
                {
                    _logger.LogWarning(
                        "Connection REFUSED: address {RemoteIp} is not in the allowlist ({Suppressed} more refused since the last entry).",
                        key, suppressed);
                }
            }
            else if (!_limiter.TryEnter(context))
            {
                if (_refusedLog.ShouldLog("limit", out var suppressed))
                {
                    _logger.LogWarning(
                        "Connection from {RemoteIp} closed: the limit of {Max} concurrent sessions is reached ({Suppressed} more closed since the last entry).",
                        remote.Address, _limiter.Maximum, suppressed);
                }
            }
            else
            {
                if (listed)
                    context.Properties.Remove(SessionKeys.MustAuthenticate);
                else
                    context.Properties[SessionKeys.MustAuthenticate] = true;

                _logger.LogDebug("Connection accepted from {RemoteIp}:{RemotePort} (in the allowlist: {Listed})", remote.Address, remote.Port, listed);
                return pipe;
            }

            try { pipe.Dispose(); } catch (Exception) { /* already closed by the peer */ }

            // The inner listener adds both keys again on the next accept and throws if one is still there:
            // leaving either behind would make the NEXT connection fail and stay open without an answer.
            context.Properties.Remove(EndpointListener.RemoteEndPointKey);
            context.Properties.Remove(EndpointListener.LocalEndPointKey);
        }
    }

    private static IPEndPoint? GetRemoteEndPoint(ISessionContext context)
        => context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value)
            ? value as IPEndPoint
            : null;

    public void Dispose() => _inner.Dispose();
}

/// <summary>Counts the sessions in progress and refuses new ones above the configured maximum.</summary>
public sealed class SessionLimiter
{
    private const string CountedKey = "SmtpRelay:SessionCounted";
    private int _current;

    public SessionLimiter(int maximum) => Maximum = maximum;

    public int Maximum { get; }
    public int Current => Volatile.Read(ref _current);

    public bool TryEnter(ISessionContext context)
    {
        if (Interlocked.Increment(ref _current) > Maximum)
        {
            Interlocked.Decrement(ref _current);
            return false;
        }
        context.Properties[CountedKey] = true;
        return true;
    }

    /// <summary>Called when a session ends, however it ends. Sessions that were never counted are ignored.</summary>
    public void Exit(ISessionContext context)
    {
        if (context.Properties.Remove(CountedKey))
            Interlocked.Decrement(ref _current);
    }
}

/// <summary>
/// Lets one entry per key through in each interval and counts the suppressed ones, so that a client that
/// keeps knocking cannot flood the log.
/// </summary>
public sealed class ThrottledLog
{
    private const int MaxKeys = 2048;
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, (DateTime Last, int Suppressed)> _state = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ThrottledLog(TimeSpan interval) => _interval = interval;

    public bool ShouldLog(string key, out int suppressed)
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            if (_state.TryGetValue(key, out var entry) && now - entry.Last < _interval)
            {
                _state[key] = (entry.Last, entry.Suppressed + 1);
                suppressed = 0;
                return false;
            }

            if (_state.Count >= MaxKeys)
                _state.Clear();

            suppressed = entry.Suppressed;
            _state[key] = (now, 0);
            return true;
        }
    }
}
