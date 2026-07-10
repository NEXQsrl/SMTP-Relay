using System.Net;
using System.Net.Sockets;

namespace SmtpRelay.Security;

/// <summary>
/// Allowlist di indirizzi IP e reti CIDR (IPv4 e IPv6). Immutabile e thread-safe.
/// </summary>
public sealed class IpAllowList
{
    private readonly IReadOnlyList<IpRange> _ranges;
    private readonly bool _rejectWhenEmpty;

    public IpAllowList(IEnumerable<string> entries, bool rejectWhenEmpty)
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

    /// <summary>Analizza una voce "IP" oppure "IP/prefisso".</summary>
    public static bool TryParse(string entry, out IpRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(entry))
            return false;

        entry = entry.Trim();
        var slash = entry.IndexOf('/');
        string ipPart = slash >= 0 ? entry[..slash] : entry;
        string? prefixPart = slash >= 0 ? entry[(slash + 1)..] : null;

        if (!IPAddress.TryParse(ipPart, out var ip))
            return false;

        int maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = maxPrefix;
        if (prefixPart is not null)
        {
            if (!int.TryParse(prefixPart, out prefix) || prefix < 0 || prefix > maxPrefix)
                return false;
        }

        range = new IpRange(ip, prefix);
        return true;
    }
}

/// <summary>Rappresenta un singolo IP o rete CIDR normalizzata.</summary>
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
        var candidate = address;

        if (candidate.AddressFamily != _family)
        {
            // Gestisce gli indirizzi IPv4 mappati in IPv6 (::ffff:a.b.c.d).
            if (_family == AddressFamily.InterNetwork && candidate.IsIPv4MappedToIPv6)
                candidate = candidate.MapToIPv4();
            else if (_family == AddressFamily.InterNetworkV6 && candidate.AddressFamily == AddressFamily.InterNetwork)
                candidate = candidate.MapToIPv6();

            if (candidate.AddressFamily != _family)
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
