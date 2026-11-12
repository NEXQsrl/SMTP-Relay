using System.Net;
using System.Net.Sockets;
using SmtpRelay.Security;

namespace SmtpRelay.Monitoring;

/// <summary>
/// The monitoring systems allowed to query the SNMP agent: IP addresses, CIDR networks and DNS names.
/// A request carries only an address, so the names are resolved to addresses when the agent starts and again
/// at intervals (<see cref="Refresh"/>). A name that cannot be resolved keeps the addresses it had before.
/// </summary>
public sealed class SnmpManagerList
{
    private readonly IpAllowList _addresses;
    private readonly string[] _names;
    private readonly Func<string, IPAddress[]> _resolve;
    private readonly Dictionary<string, IPAddress[]> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private volatile HashSet<IPAddress> _resolved = new();

    /// <param name="entries">Addresses, networks and names. Entries that are none of them are ignored.</param>
    /// <param name="resolve">Resolves a name to its addresses (DNS by default); it throws when the name is unknown.</param>
    public SnmpManagerList(IEnumerable<string>? entries, Func<string, IPAddress[]>? resolve = null)
    {
        var list = (entries ?? Enumerable.Empty<string>()).Select(e => (e ?? "").Trim()).Where(e => e.Length > 0).ToList();
        _addresses = new IpAllowList(list.Where(e => IpAllowList.TryParse(e, out _)), rejectWhenEmpty: true);
        _names = list.Where(IsHostName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _resolve = resolve ?? Dns.GetHostAddresses;
    }

    /// <summary>Number of usable entries (addresses, networks and names).</summary>
    public int Count => _addresses.Count + _names.Length;

    public IReadOnlyList<string> Names => _names;

    /// <summary>True when the entry is an IP address, a CIDR network or a DNS name.</summary>
    public static bool IsValidEntry(string? entry) => IpAllowList.TryParse(entry, out _) || IsHostName(entry);

    /// <summary>
    /// True for a DNS host name such as "nms01" or "zabbix.contoso.local". A name must contain a letter, so
    /// that a mistyped address ("192.168.1") is reported instead of being looked up.
    /// </summary>
    public static bool IsHostName(string? value)
    {
        value = value?.Trim() ?? "";
        return value.Length is > 0 and <= 253 &&
               value.Any(char.IsAsciiLetter) &&
               !value.Contains(':') &&
               Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
            return false;
        if (_addresses.IsAllowed(address))
            return true;
        return _resolved.Contains(Normalize(address));
    }

    /// <summary>
    /// Resolves the names again. Returns the names that could not be resolved, with the reason; they keep
    /// the addresses of the last successful resolution, so that a DNS outage does not stop the monitoring.
    /// </summary>
    public IReadOnlyList<(string Name, string Reason)> Refresh()
    {
        var failures = new List<(string, string)>();
        if (_names.Length == 0)
            return failures;

        lock (_sync)
        {
            foreach (var name in _names)
            {
                try
                {
                    var found = _resolve(name).Select(Normalize).Distinct().ToArray();
                    if (found.Length == 0)
                        failures.Add((name, "the name has no address"));
                    else
                        _byName[name] = found;
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
                {
                    failures.Add((name, ex.Message));
                }
            }
            _resolved = new HashSet<IPAddress>(_byName.Values.SelectMany(a => a));
        }
        return failures;
    }

    /// <summary>The addresses a name currently stands for (empty until it has been resolved).</summary>
    public IReadOnlyList<IPAddress> AddressesOf(string name)
    {
        lock (_sync)
            return _byName.TryGetValue(name, out var found) ? found : Array.Empty<IPAddress>();
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
