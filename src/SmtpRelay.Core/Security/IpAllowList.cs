using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace SmtpRelay.Security;

/// <summary>Allowlist of IP addresses and CIDR networks (IPv4 and IPv6). Immutable and thread-safe.</summary>
public sealed class IpAllowList
{
    private readonly IReadOnlyList<IpRange> _ranges;
    private readonly bool _rejectWhenEmpty;

    public IpAllowList(IEnumerable<string>? entries, bool rejectWhenEmpty)
    {
        var list = new List<IpRange>();
        foreach (var entry in entries ?? Enumerable.Empty<string>())
        {
            if (TryParse(entry, out var range))
                list.Add(range);
        }
        _ranges = list;
        _rejectWhenEmpty = rejectWhenEmpty;
    }

    public int Count => _ranges.Count;

    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
            return false;
        if (_ranges.Count == 0)
            return !_rejectWhenEmpty;

        foreach (var range in _ranges)
        {
            if (range.Contains(address))
                return true;
        }
        return false;
    }

    /// <summary>Parses an entry "IP" or "IP/prefix".</summary>
    public static bool TryParse(string? entry, out IpRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(entry))
            return false;

        entry = entry.Trim();
        var slash = entry.IndexOf('/');
        string ipPart = slash >= 0 ? entry[..slash] : entry;
        string? prefixPart = slash >= 0 ? entry[(slash + 1)..] : null;

        // IPAddress.TryParse also accepts forms such as "1" or "10.1": require a complete address.
        if (!IPAddress.TryParse(ipPart, out var ip))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork && ipPart.Count(c => c == '.') != 3)
            return false;
        if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;

        int maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = maxPrefix;
        if (prefixPart is not null)
        {
            if (!int.TryParse(prefixPart, NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > maxPrefix)
                return false;
        }

        range = new IpRange(ip, prefix);
        return true;
    }
}

/// <summary>A single IP address or a normalized CIDR network.</summary>
public readonly struct IpRange
{
    private readonly byte[] _network;
    private readonly int _prefix;
    private readonly AddressFamily _family;

    public IpRange(IPAddress address, int prefixLength)
    {
        _family = address.AddressFamily;
        _prefix = prefixLength;
        _network = MaskBytes(address.GetAddressBytes(), prefixLength);
    }

    public bool Contains(IPAddress address)
    {
        if (_network is null)
            return false;

        var candidate = address;
        if (candidate.AddressFamily != _family)
        {
            // IPv4 addresses mapped into IPv6 (::ffff:a.b.c.d) match IPv4 entries. The opposite is not done:
            // an IPv6 network must never match a plain IPv4 client by accident.
            if (_family == AddressFamily.InterNetwork && candidate.IsIPv4MappedToIPv6)
                candidate = candidate.MapToIPv4();
            else
                return false;
        }

        var masked = MaskBytes(candidate.GetAddressBytes(), _prefix);
        return masked.AsSpan().SequenceEqual(_network);
    }

    private static byte[] MaskBytes(byte[] bytes, int prefixLength)
    {
        var result = new byte[bytes.Length];
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes && i < bytes.Length; i++)
            result[i] = bytes[i];

        if (remainingBits > 0 && fullBytes < bytes.Length)
        {
            int mask = (byte)(0xFF << (8 - remainingBits));
            result[fullBytes] = (byte)(bytes[fullBytes] & mask);
        }

        return result;
    }
}
