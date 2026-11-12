using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SmtpRelay.Migration;

/// <summary>What the relay restrictions of an IIS 6 SMTP virtual server contain.</summary>
/// <param name="Entries">Granted computers, as single addresses or CIDR networks.</param>
/// <param name="AllExceptList">True when IIS is set to "All except the list below": there is nothing to allow.</param>
/// <param name="SkippedNames">Entries given as domain names, which an IP allowlist cannot express.</param>
/// <param name="Source">Where the list was read from, for the message shown to the administrator.</param>
public sealed record Iis6RelayImport(IReadOnlyList<string> Entries, bool AllExceptList, int SkippedNames, string Source);

/// <summary>
/// Reads the "Relay restrictions" list of the IIS 6 SMTP service (the list of computers that may relay), so
/// that it can be copied into the allowlist when the relay replaces IIS. The list is asked to IIS through
/// ADSI; when that is not available it is read from the metabase file.
/// </summary>
public static partial class Iis6RelayList
{
    private const uint OffsetMask = 0x7FFFFFFF;

    /// <summary>Key of <see cref="Exception.Data"/> that holds the value that could not be decoded.</summary>
    public const string RawValueKey = "RelayIpList";

    [GeneratedRegex(@"^/LM/SmtpSvc/(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualServerRegex();

    public static string DefaultMetabasePath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "MetaBase.xml");

    /// <summary>
    /// Reads the list from this computer. Returns null when the IIS 6 SMTP service is not installed here.
    /// Throws when it is installed but its list cannot be understood.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Iis6RelayImport? ReadLocal()
    {
        var fromAdsi = TryReadAdsi();
        if (fromAdsi is not null)
            return fromAdsi;

        var path = DefaultMetabasePath;
        return File.Exists(path) ? ReadMetabase(path) : null;
    }

    // ----- ADSI (IIS://localhost/SmtpSvc/1) --------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static Iis6RelayImport? TryReadAdsi()
    {
        try
        {
            dynamic server = Marshal.BindToMoniker("IIS://localhost/SmtpSvc/1");
            dynamic list = server.Get("RelayIpList");
            bool grantByDefault = (bool)list.GrantByDefault;

            var entries = new List<string>();
            foreach (var value in ToStrings((object?)list.IPGrant))
            {
                if (TryParseAdsiEntry(value, out var entry) && !entries.Contains(entry))
                    entries.Add(entry);
            }
            int names = ToStrings((object?)list.DomainGrant).Count(v => v.Trim().Length > 0);

            // "All except the list below" grants by default and keeps a list of denied computers instead.
            return new Iis6RelayImport(entries, grantByDefault && entries.Count == 0, names, "IIS (virtual server 1)");
        }
        catch (Exception)
        {
            // No IIS 6 ADSI provider on this computer, or no SMTP virtual server: try the metabase file.
            return null;
        }
    }

    private static IEnumerable<string> ToStrings(object? value) => value switch
    {
        null => Array.Empty<string>(),
        string text => new[] { text },
        System.Collections.IEnumerable items => items.Cast<object?>().Select(i => i?.ToString() ?? "").ToArray(),
        _ => Array.Empty<string>()
    };

    /// <summary>Parses an entry as ADSI returns it: "10.0.0.5" or "10.0.0.0, 255.255.255.0".</summary>
    public static bool TryParseAdsiEntry(string? value, out string entry)
    {
        entry = "";
        var parts = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var address))
            return false;

        byte[] mask;
        if (parts.Length == 2)
        {
            if (!IPAddress.TryParse(parts[1], out var maskAddress) || maskAddress.AddressFamily != address.AddressFamily)
                return false;
            mask = maskAddress.GetAddressBytes();
        }
        else
        {
            mask = Enumerable.Repeat((byte)0xFF, address.GetAddressBytes().Length).ToArray();
        }

        return TryFormat(address.GetAddressBytes(), mask, out entry);
    }

    // ----- metabase file ---------------------------------------------------------------------------------

    /// <summary>Reads the list of every SMTP virtual server found in a MetaBase.xml file. Null when there is none.</summary>
    public static Iis6RelayImport? ReadMetabase(string path)
    {
        XDocument document;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            document = XDocument.Load(stream);

        var entries = new List<string>();
        int names = 0, servers = 0;
        bool allExcept = false;

        foreach (var element in document.Descendants())
        {
            var location = (string?)element.Attribute("Location");
            if (location is null || !VirtualServerRegex().IsMatch(location.Trim()))
                continue;

            servers++;
            var hex = (string?)element.Attribute("RelayIpList");
            if (string.IsNullOrWhiteSpace(hex))
                continue;

            Iis6RelayImport parsed;
            try
            {
                parsed = ParseBlob(FromHex(hex));
            }
            catch (InvalidDataException ex)
            {
                // Kept for the log of the console: the value holds addresses and masks only, and it is what
                // is needed to understand why it was not recognized.
                var digits = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
                ex.Data[RawValueKey] = $"{location.Trim()} RelayIpList={(digits.Length > 4000 ? digits[..4000] + "..." : digits)}";
                throw;
            }
            names += parsed.SkippedNames;
            allExcept |= parsed.AllExceptList;
            foreach (var entry in parsed.Entries)
            {
                if (!entries.Contains(entry))
                    entries.Add(entry);
            }
        }

        if (servers == 0)
            return null;
        return new Iis6RelayImport(entries, allExcept && entries.Count == 0, names, path);
    }

    private static byte[] FromHex(string hex)
    {
        var digits = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
        try { return Convert.FromHexString(digits); }
        catch (FormatException ex) { throw new InvalidDataException("The relay list in the IIS metabase is not in the expected format.", ex); }
    }

    /// <summary>
    /// Decodes the binary form of the list (the metabase property RelayIpList). It starts with the offsets of
    /// four lists (denied addresses, granted addresses, denied names, granted names); an address list holds
    /// groups of addresses that share a subnet mask, each group stored as the mask followed by the addresses.
    /// The counter of a group includes its mask, while the counter of the list counts the addresses only.
    /// </summary>
    public static Iis6RelayImport ParseBlob(byte[] blob)
    {
        if (blob.Length == 0)
            return new Iis6RelayImport(Array.Empty<string>(), false, 0, "metabase");
        if (blob.Length < 20)
            throw Unrecognized();

        var denied = ReadAddressList(blob, ReadOffset(blob, 0));
        var granted = ReadAddressList(blob, ReadOffset(blob, 4));
        int grantedNames = ReadNameCount(blob, ReadOffset(blob, 12));

        return new Iis6RelayImport(granted, granted.Count == 0 && grantedNames == 0 && denied.Count > 0, grantedNames, "metabase");
    }

    private static List<string> ReadAddressList(byte[] blob, int offset)
    {
        var result = new List<string>();
        if (offset == 0)
            return result;
        if (offset + 8 > blob.Length)
            throw Unrecognized();

        uint groups = ReadUInt32(blob, offset);
        uint total = ReadUInt32(blob, offset + 4);
        if (groups > 256 || offset + 8 + (long)groups * 20 > blob.Length)
            throw Unrecognized();

        // IIS counts the mask as the first item of each group, so the counters of the groups add up to the
        // total of the list plus one per group. A list whose group counters add up to the total itself is
        // read as holding addresses only.
        long sum = 0;
        for (int g = 0; g < groups; g++)
            sum += ReadUInt32(blob, offset + 8 + g * 20 + 4);
        bool countIncludesMask = sum != total;

        for (int g = 0; g < groups; g++)
        {
            int p = offset + 8 + g * 20;

            uint family = ReadUInt32(blob, p);
            uint count = ReadUInt32(blob, p + 4);
            if (countIncludesMask)
            {
                if (count == 0)
                    continue; // not even a mask: an empty group
                count--;
            }
            uint fullBytes = ReadUInt32(blob, p + 8);
            uint lastByte = ReadUInt32(blob, p + 12);
            int first = ReadOffset(blob, p + 16);

            int length = family switch
            {
                (uint)AddressFamily.InterNetwork => 4,
                (uint)AddressFamily.InterNetworkV6 => 16,
                _ => 0
            };
            if (length == 0)
                continue; // a family the relay cannot use
            if (count > 100_000 || fullBytes > length || first + (long)length * (count + 1) > blob.Length)
                throw Unrecognized();

            var mask = blob.AsSpan(first, length).ToArray();
            if (PrefixLength(mask) < 0)
                mask = MaskFrom(length, (int)fullBytes, (byte)lastByte);

            for (int i = 0; i < count; i++)
            {
                var address = blob.AsSpan(first + length * (i + 1), length).ToArray();
                if (TryFormat(address, mask, out var entry) && !result.Contains(entry))
                    result.Add(entry);
            }
        }
        return result;
    }

    private static int ReadNameCount(byte[] blob, int offset)
    {
        if (offset == 0 || offset + 8 > blob.Length)
            return 0;
        // Two counters open the list: groups and names. Either one above zero means that names are present.
        uint names = Math.Max(ReadUInt32(blob, offset), ReadUInt32(blob, offset + 4));
        return names > 100_000 ? 0 : (int)names;
    }

    private static byte[] MaskFrom(int length, int fullBytes, byte lastByte)
    {
        var mask = new byte[length];
        for (int i = 0; i < fullBytes && i < length; i++)
            mask[i] = 0xFF;
        if (fullBytes < length)
            mask[fullBytes] = lastByte;
        return mask;
    }

    /// <summary>Number of leading one bits of a subnet mask, or -1 when the ones are not contiguous.</summary>
    private static int PrefixLength(byte[] mask)
    {
        int prefix = 0;
        bool ended = false;
        foreach (var b in mask)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool one = (b & (1 << bit)) != 0;
                if (one && ended)
                    return -1;
                if (one)
                    prefix++;
                else
                    ended = true;
            }
        }
        return prefix;
    }

    private static bool TryFormat(byte[] address, byte[] mask, out string entry)
    {
        entry = "";
        int prefix = PrefixLength(mask);
        if (prefix < 0 || address.Length != mask.Length)
            return false;

        var network = new byte[address.Length];
        for (int i = 0; i < address.Length; i++)
            network[i] = (byte)(address[i] & mask[i]);

        var text = new IPAddress(network).ToString();
        entry = prefix == address.Length * 8 ? text : text + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static uint ReadUInt32(byte[] blob, int index) => BitConverter.ToUInt32(blob, index);

    private static int ReadOffset(byte[] blob, int index)
    {
        uint offset = ReadUInt32(blob, index) & OffsetMask;
        if (offset > blob.Length)
            throw Unrecognized();
        return (int)offset;
    }

    private static InvalidDataException Unrecognized()
        => new("The relay list of IIS is not in the expected format and was not imported.");
}
