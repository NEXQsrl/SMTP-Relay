using System.Security.Cryptography;
using System.Text;

namespace SmtpRelay.Monitoring;

/// <summary>A value of an SNMP variable binding: the BER tag and its content octets.</summary>
public readonly record struct SnmpValue(byte Tag, byte[] Content)
{
    public const byte IntegerTag = 0x02;
    public const byte OctetStringTag = 0x04;
    public const byte NullTag = 0x05;
    public const byte ObjectIdTag = 0x06;
    public const byte Gauge32Tag = 0x42;
    public const byte TimeTicksTag = 0x43;
    public const byte NoSuchObjectTag = 0x80;
    public const byte NoSuchInstanceTag = 0x81;
    public const byte EndOfMibViewTag = 0x82;

    public static SnmpValue Null { get; } = new(NullTag, Array.Empty<byte>());
    public static SnmpValue NoSuchObject { get; } = new(NoSuchObjectTag, Array.Empty<byte>());
    public static SnmpValue NoSuchInstance { get; } = new(NoSuchInstanceTag, Array.Empty<byte>());
    public static SnmpValue EndOfMibView { get; } = new(EndOfMibViewTag, Array.Empty<byte>());

    public static SnmpValue Gauge32(long value) => new(Gauge32Tag, Ber.EncodeUnsigned((uint)Math.Clamp(value, 0, uint.MaxValue)));
    public static SnmpValue TimeTicks(long value) => new(TimeTicksTag, Ber.EncodeUnsigned((uint)(value & 0xFFFFFFFF)));
    public static SnmpValue ObjectId(IReadOnlyList<uint> oid) => new(ObjectIdTag, Ber.EncodeOid(oid));

    /// <summary>A DisplayString: printable ASCII, at most 255 characters.</summary>
    public static SnmpValue DisplayString(string text)
    {
        var clean = new string(text.Select(c => c is >= ' ' and <= '~' ? c : '?').Take(255).ToArray());
        return new(OctetStringTag, Encoding.ASCII.GetBytes(clean));
    }
}

/// <summary>A scalar object the agent answers for: the full instance OID (ending in .0) and how to read its value.</summary>
public sealed record SnmpScalar(uint[] Oid, Func<SnmpValue> Read);

/// <summary>
/// Read-only SNMP v1 / v2c responder over a fixed set of scalars. It turns one request datagram into one
/// response datagram and has no side effects, so the network part stays trivial. Anything that is not a
/// well-formed request with the right community is dropped without an answer.
/// </summary>
public sealed class SnmpResponder
{
    private const int MaxRequestBytes = 4096;
    private const int MaxRequestVarBinds = 32;
    private const int MaxResponseVarBinds = 64;

    private const byte GetRequest = 0xA0;
    private const byte GetNextRequest = 0xA1;
    private const byte Response = 0xA2;
    private const byte SetRequest = 0xA3;
    private const byte GetBulkRequest = 0xA5;

    private const int NoSuchName = 2;
    private const int NotWritable = 17;

    private readonly SnmpScalar[] _objects;
    private readonly Func<string?> _community;
    private long _badCommunity;

    /// <param name="community">Returns the community the managers must present, or null when none is set (every request is then dropped).</param>
    public SnmpResponder(IEnumerable<SnmpScalar> objects, Func<string?> community)
    {
        _objects = objects.OrderBy(o => o.Oid, OidComparer.Instance).ToArray();
        _community = community;
    }

    /// <summary>Requests dropped because of a wrong community.</summary>
    public long BadCommunityCount => Interlocked.Read(ref _badCommunity);

    /// <summary>Returns the response datagram, or null when the request must not be answered.</summary>
    public byte[]? Handle(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length > MaxRequestBytes)
            return null;

        var outer = new BerReader(datagram);
        if (!outer.TryRead(out var tag, out var message) || tag != 0x30)
            return null;

        var reader = new BerReader(message);
        if (!reader.TryRead(out tag, out var versionBytes) || tag != SnmpValue.IntegerTag || !Ber.TryDecodeInteger(versionBytes, out var version))
            return null;
        if (version is not (0 or 1)) // v1 and v2c only
            return null;
        if (!reader.TryRead(out tag, out var community) || tag != SnmpValue.OctetStringTag)
            return null;
        if (!reader.TryRead(out var pduType, out var pdu))
            return null;
        if (pduType is not (GetRequest or GetNextRequest or SetRequest or GetBulkRequest))
            return null;
        if (pduType == GetBulkRequest && version == 0)
            return null;

        var expected = _community();
        if (string.IsNullOrEmpty(expected) ||
            !CryptographicOperations.FixedTimeEquals(community, Encoding.UTF8.GetBytes(expected)))
        {
            Interlocked.Increment(ref _badCommunity);
            return null;
        }

        var body = new BerReader(pdu);
        if (!body.TryRead(out tag, out var requestId) || tag != SnmpValue.IntegerTag || requestId.Length is < 1 or > 5)
            return null;
        if (!body.TryRead(out tag, out var field1) || tag != SnmpValue.IntegerTag || !Ber.TryDecodeInteger(field1, out var nonRepeaters))
            return null;
        if (!body.TryRead(out tag, out var field2) || tag != SnmpValue.IntegerTag || !Ber.TryDecodeInteger(field2, out var maxRepetitions))
            return null;
        if (!body.TryRead(out tag, out var list) || tag != 0x30)
            return null;

        var request = new List<(uint[] Oid, SnmpValue Value)>();
        var bindings = new BerReader(list);
        while (!bindings.AtEnd)
        {
            if (request.Count == MaxRequestVarBinds)
                return null;
            if (!bindings.TryRead(out tag, out var binding) || tag != 0x30)
                return null;
            var pair = new BerReader(binding);
            if (!pair.TryRead(out tag, out var name) || tag != SnmpValue.ObjectIdTag || !Ber.TryDecodeOid(name, out var oid))
                return null;
            if (!pair.TryRead(out var valueTag, out var value))
                return null;
            request.Add((oid, new SnmpValue(valueTag, value.ToArray())));
        }

        int errorStatus = 0, errorIndex = 0;
        var answer = new List<(uint[] Oid, SnmpValue Value)>(request.Count);

        switch (pduType)
        {
            case SetRequest:
                // Nothing is writable.
                errorStatus = version == 0 ? NoSuchName : NotWritable;
                errorIndex = request.Count > 0 ? 1 : 0;
                answer.AddRange(request);
                break;

            case GetBulkRequest:
                int single = Math.Clamp(nonRepeaters, 0, request.Count);
                for (int i = 0; i < single; i++)
                    answer.Add(Next(request[i].Oid));

                var cursors = request.Skip(single).Select(r => r.Oid).ToArray();
                for (int round = 0; round < Math.Clamp(maxRepetitions, 0, MaxResponseVarBinds) && cursors.Length > 0; round++)
                {
                    bool allEnded = true;
                    for (int i = 0; i < cursors.Length && answer.Count < MaxResponseVarBinds; i++)
                    {
                        var next = Next(cursors[i]);
                        answer.Add(next);
                        cursors[i] = next.Oid;
                        allEnded &= next.Value.Tag == SnmpValue.EndOfMibViewTag;
                    }
                    if (allEnded || answer.Count >= MaxResponseVarBinds)
                        break;
                }
                break;

            default:
                for (int i = 0; i < request.Count; i++)
                {
                    var item = pduType == GetRequest ? Get(request[i].Oid) : Next(request[i].Oid);
                    bool missing = item.Value.Tag is SnmpValue.NoSuchObjectTag or SnmpValue.NoSuchInstanceTag or SnmpValue.EndOfMibViewTag;
                    if (missing && version == 0)
                    {
                        // SNMPv1 has no exception values: the whole request fails and names the variable.
                        errorStatus = NoSuchName;
                        errorIndex = i + 1;
                        answer.Clear();
                        answer.AddRange(request.Select(r => (r.Oid, SnmpValue.Null)));
                        break;
                    }
                    answer.Add(item);
                }
                break;
        }

        var varBinds = answer
            .Select(a => Ber.Tlv(0x30, Ber.Tlv(SnmpValue.ObjectIdTag, Ber.EncodeOid(a.Oid)), Ber.Tlv(a.Value.Tag, a.Value.Content)))
            .ToArray();

        return Ber.Tlv(0x30,
            Ber.Tlv(SnmpValue.IntegerTag, Ber.EncodeInteger(version)),
            Ber.Tlv(SnmpValue.OctetStringTag, community.ToArray()),
            Ber.Tlv(Response,
                Ber.Tlv(SnmpValue.IntegerTag, requestId.ToArray()),
                Ber.Tlv(SnmpValue.IntegerTag, Ber.EncodeInteger(errorStatus)),
                Ber.Tlv(SnmpValue.IntegerTag, Ber.EncodeInteger(errorIndex)),
                Ber.Tlv(0x30, varBinds)));
    }

    private (uint[] Oid, SnmpValue Value) Get(uint[] oid)
    {
        foreach (var o in _objects)
        {
            if (OidComparer.Instance.Compare(o.Oid, oid) == 0)
                return (oid, o.Read());
        }

        // The object exists but the instance asked for does not (for example the OID without the trailing .0).
        bool knownObject = _objects.Any(o => IsPrefix(o.Oid.AsSpan(0, o.Oid.Length - 1), oid));
        return (oid, knownObject ? SnmpValue.NoSuchInstance : SnmpValue.NoSuchObject);
    }

    private (uint[] Oid, SnmpValue Value) Next(uint[] oid)
    {
        foreach (var o in _objects)
        {
            if (OidComparer.Instance.Compare(o.Oid, oid) > 0)
                return (o.Oid, o.Read());
        }
        return (oid, SnmpValue.EndOfMibView);
    }

    private static bool IsPrefix(ReadOnlySpan<uint> prefix, ReadOnlySpan<uint> oid)
        => oid.Length >= prefix.Length && oid[..prefix.Length].SequenceEqual(prefix);

    private sealed class OidComparer : IComparer<uint[]>
    {
        public static readonly OidComparer Instance = new();

        public int Compare(uint[]? x, uint[]? y)
        {
            x ??= Array.Empty<uint>();
            y ??= Array.Empty<uint>();
            int common = Math.Min(x.Length, y.Length);
            for (int i = 0; i < common; i++)
            {
                if (x[i] != y[i])
                    return x[i] < y[i] ? -1 : 1;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}

/// <summary>The small part of the Basic Encoding Rules that SNMP needs.</summary>
internal static class Ber
{
    private const int MaxOidLength = 128;

    public static byte[] Tlv(byte tag, params byte[][] parts)
    {
        int length = parts.Sum(p => p.Length);
        int header = length < 0x80 ? 2 : length <= 0xFF ? 3 : length <= 0xFFFF ? 4 : 5;
        var result = new byte[header + length];
        result[0] = tag;
        switch (header)
        {
            case 2: result[1] = (byte)length; break;
            case 3: result[1] = 0x81; result[2] = (byte)length; break;
            case 4: result[1] = 0x82; result[2] = (byte)(length >> 8); result[3] = (byte)length; break;
            default: result[1] = 0x83; result[2] = (byte)(length >> 16); result[3] = (byte)(length >> 8); result[4] = (byte)length; break;
        }

        int offset = header;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    /// <summary>Two's complement, shortest form.</summary>
    public static byte[] EncodeInteger(int value)
    {
        int length = 4;
        while (length > 1)
        {
            int top = (value >> ((length - 1) * 8)) & 0xFF;
            int nextSign = (value >> ((length - 2) * 8)) & 0x80;
            if ((top == 0x00 && nextSign == 0) || (top == 0xFF && nextSign != 0))
                length--;
            else
                break;
        }

        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(value >> ((length - 1 - i) * 8));
        return bytes;
    }

    /// <summary>An unsigned 32-bit value (Gauge32, TimeTicks): a leading zero keeps it positive.</summary>
    public static byte[] EncodeUnsigned(uint value)
    {
        int length = value <= 0x7F ? 1 : value <= 0x7FFF ? 2 : value <= 0x7FFFFF ? 3 : value <= 0x7FFFFFFF ? 4 : 5;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int shift = (length - 1 - i) * 8;
            bytes[i] = shift >= 32 ? (byte)0 : (byte)(value >> shift);
        }
        return bytes;
    }

    public static bool TryDecodeInteger(ReadOnlySpan<byte> content, out int value)
    {
        value = 0;
        if (content.Length is < 1 or > 5)
            return false;

        long result = (content[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in content)
            result = (result << 8) | b;
        value = (int)Math.Clamp(result, int.MinValue, int.MaxValue);
        return true;
    }

    public static byte[] EncodeOid(IReadOnlyList<uint> oid)
    {
        if (oid.Count < 2)
            throw new ArgumentException("An object identifier has at least two arcs.", nameof(oid));

        var bytes = new List<byte>(oid.Count + 4);
        AppendBase128(bytes, (ulong)oid[0] * 40 + oid[1]);
        for (int i = 2; i < oid.Count; i++)
            AppendBase128(bytes, oid[i]);
        return bytes.ToArray();
    }

    public static bool TryDecodeOid(ReadOnlySpan<byte> content, out uint[] oid)
    {
        oid = Array.Empty<uint>();
        if (content.Length == 0 || (content[^1] & 0x80) != 0)
            return false;

        var arcs = new List<uint>(content.Length + 1);
        ulong current = 0;
        bool first = true;
        foreach (var b in content)
        {
            current = (current << 7) | (uint)(b & 0x7F);
            if (current > 0x1_0000_004F) // largest first sub-identifier: 2 * 40 + 0xFFFFFFFF
                return false;
            if ((b & 0x80) != 0)
                continue;

            if (first)
            {
                uint root = current < 40 ? 0u : current < 80 ? 1u : 2u;
                ulong second = current - root * 40;
                if (second > uint.MaxValue)
                    return false;
                arcs.Add(root);
                arcs.Add((uint)second);
                first = false;
            }
            else
            {
                if (current > uint.MaxValue)
                    return false;
                arcs.Add((uint)current);
            }

            if (arcs.Count > MaxOidLength)
                return false;
            current = 0;
        }

        oid = arcs.ToArray();
        return true;
    }

    private static void AppendBase128(List<byte> bytes, ulong value)
    {
        int groups = 1;
        for (ulong v = value >> 7; v != 0; v >>= 7)
            groups++;
        for (int i = groups - 1; i >= 0; i--)
            bytes.Add((byte)(((value >> (i * 7)) & 0x7F) | (i > 0 ? 0x80u : 0u)));
    }
}

/// <summary>Reads consecutive tag-length-value elements with definite lengths.</summary>
internal ref struct BerReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public BerReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public readonly bool AtEnd => _position >= _data.Length;

    public bool TryRead(out byte tag, out ReadOnlySpan<byte> content)
    {
        tag = 0;
        content = default;
        if (_data.Length - _position < 2)
            return false;

        tag = _data[_position++];
        if ((tag & 0x1F) == 0x1F) // multi-byte tags are not used by SNMP
            return false;

        int length = _data[_position++];
        if (length >= 0x80)
        {
            int count = length & 0x7F;
            if (count is 0 or > 3 || _data.Length - _position < count)
                return false;
            length = 0;
            for (int i = 0; i < count; i++)
                length = (length << 8) | _data[_position++];
        }

        if (length > _data.Length - _position)
            return false;

        content = _data.Slice(_position, length);
        _position += length;
        return true;
    }
}
