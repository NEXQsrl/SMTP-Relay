using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmtpRelay.Configuration;
using SmtpRelay.Monitoring;
using SmtpRelay.Security;

namespace SmtpRelay.Snmp;

/// <summary>
/// Built-in SNMP agent: answers read-only v1 / v2c requests from the allowed monitoring systems with
/// today's delivery figures. It runs next to the Windows SNMP service on its own UDP port. A failure here
/// never stops the relay: monitoring must not be able to take the mail flow down.
/// </summary>
public sealed class SnmpAgentService : BackgroundService
{
    private readonly SnmpOptions _options;
    private readonly SecretStore _secrets;
    private readonly SnmpManagerList _managers;
    private readonly TimeSpan _nameRefreshInterval;
    private readonly ThrottledNames _unresolved = new();
    private readonly SnmpResponder _responder;
    private readonly ILogger<SnmpAgentService> _logger;
    private readonly Stopwatch _upTime = Stopwatch.StartNew();
    private long _nextRefusedLogTicks;
    private long _reportedBadCommunity;

    public SnmpAgentService(RelayOptions options, SecretStore secrets, DeliveryCounters counters, ILogger<SnmpAgentService> logger)
        : this(options, secrets, counters, logger, null, null)
    {
    }

    /// <param name="managers">The allowed monitoring systems; built from the options when null.</param>
    /// <param name="nameRefreshInterval">How often DNS names of monitoring systems are resolved again (five minutes by default).</param>
    public SnmpAgentService(RelayOptions options, SecretStore secrets, DeliveryCounters counters, ILogger<SnmpAgentService> logger,
        SnmpManagerList? managers, TimeSpan? nameRefreshInterval)
    {
        _options = options.Snmp;
        _secrets = secrets;
        _logger = logger;
        _managers = managers ?? new SnmpManagerList(_options.AllowedManagers);
        _nameRefreshInterval = nameRefreshInterval ?? TimeSpan.FromMinutes(5);
        _responder = new SnmpResponder(
            RelayMib.CreateObjects(counters, () => _upTime.Elapsed),
            () => _secrets.Get(_options.CommunitySecret));
    }

    /// <summary>UDP port the agent is bound to (0 until it listens). Used by the tests.</summary>
    public int BoundPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The host starts the hosted services one after the other: do not hold it up.
        await Task.Yield();

        Socket socket;
        try
        {
            var address = ResolveListenAddress(_options.IpAddress);
            socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            DisableConnectionReset(socket);
            socket.Bind(new IPEndPoint(address, _options.Port));
            BoundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        catch (Exception ex) when (ex is SocketException or FormatException or ArgumentException)
        {
            _logger.LogError(
                "The SNMP agent cannot listen on UDP {Ip}:{Port}: {Reason}. Monitoring is off; the relay keeps working. " +
                "Port 161 belongs to the Windows SNMP service when that is installed: choose another port.",
                _options.IpAddress, _options.Port, ex.Message);
            return;
        }

        using (socket)
        {
            if (!_secrets.Contains(_options.CommunitySecret))
            {
                _logger.LogWarning(
                    "The SNMP agent is on but the community '{Secret}' is not in the secret store: no request is answered until it is set.",
                    _options.CommunitySecret);
            }

            _logger.LogInformation(
                "SNMP agent listening on UDP {Ip}:{Port} (v1/v2c, read-only, {Count} allowed manager entries). OID of delivered today: {DeliveredOid}, of failed today: {FailedOid}.",
                _options.IpAddress, BoundPort, _managers.Count,
                RelayMib.ToText(RelayMib.DeliveredToday), RelayMib.ToText(RelayMib.FailedToday));

            ResolveManagerNames(firstTime: true);
            var refresh = _managers.Names.Count > 0 ? RefreshManagerNamesAsync(stoppingToken) : Task.CompletedTask;

            var buffer = new byte[8192];
            EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken).ConfigureAwait(false);
                    var remote = (IPEndPoint)received.RemoteEndPoint;

                    if (!_managers.IsAllowed(remote.Address))
                    {
                        LogRefused("SNMP request from {Address} ignored: the address is not in Snmp.AllowedManagers (and is not the address of one of its DNS names).", remote.Address);
                        continue;
                    }

                    var response = _responder.Handle(buffer.AsSpan(0, received.ReceivedBytes));
                    if (response is null)
                    {
                        var bad = _responder.BadCommunityCount;
                        if (bad != Interlocked.Exchange(ref _reportedBadCommunity, bad))
                            LogRefused("SNMP request from {Address} ignored: wrong community.", remote.Address);
                        continue;
                    }

                    await socket.SendToAsync(response, SocketFlags.None, remote, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // One datagram that cannot be received or answered is not a reason to stop.
                    _logger.LogDebug("SNMP socket error: {Reason}", ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The SNMP agent could not answer a request.");
                }
            }

            try { await refresh.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _logger.LogInformation("SNMP agent stopped.");
    }

    /// <summary>An IP address as it is, or the address of a DNS name of this computer (IPv4 when it has one).</summary>
    private static IPAddress ResolveListenAddress(string value)
    {
        value = value.Trim();
        if (IPAddress.TryParse(value, out var address))
            return address;

        var found = Dns.GetHostAddresses(value);
        return found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? found.FirstOrDefault()
               ?? throw new ArgumentException($"the name '{value}' has no address");
    }

    private async Task RefreshManagerNamesAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_nameRefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                ResolveManagerNames(firstTime: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The DNS names of the monitoring systems could not be resolved again.");
            }
        }
    }

    // A name that does not resolve is reported when it starts failing and when it works again, not at every attempt.
    private void ResolveManagerNames(bool firstTime)
    {
        if (_managers.Names.Count == 0)
            return;

        var failures = _managers.Refresh();
        foreach (var name in _managers.Names)
        {
            var failure = failures.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            var addresses = string.Join(", ", _managers.AddressesOf(name));
            if (failure.Name is not null)
            {
                if (_unresolved.Add(name))
                {
                    _logger.LogWarning(
                        "The monitoring system '{Name}' (Snmp.AllowedManagers) cannot be resolved: {Reason}. {Fallback}",
                        name, failure.Reason,
                        addresses.Length == 0 ? "Requests from it are ignored until the name resolves." : $"Its last known address is kept: {addresses}.");
                }
            }
            else if (_unresolved.Remove(name) || firstTime)
            {
                _logger.LogInformation("Monitoring system '{Name}' resolves to {Addresses}.", name, addresses);
            }
        }
    }

    private sealed class ThrottledNames
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        public bool Add(string name) { lock (_names) return _names.Add(name); }
        public bool Remove(string name) { lock (_names) return _names.Remove(name); }
    }

    // At most one line a minute: a scanner must not be able to fill the log.
    private void LogRefused(string message, IPAddress address)
    {
        var now = DateTime.UtcNow.Ticks;
        var due = Interlocked.Read(ref _nextRefusedLogTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextRefusedLogTicks, now + TimeSpan.TicksPerMinute, due) != due)
            return;
        _logger.LogWarning(message, address);
    }

    // On Windows an ICMP "port unreachable" caused by an earlier answer makes the next receive fail with
    // ConnectionReset. The agent does not care whether a manager is still there.
    private static void DisableConnectionReset(Socket socket)
    {
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try
        {
            socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
    }
}
