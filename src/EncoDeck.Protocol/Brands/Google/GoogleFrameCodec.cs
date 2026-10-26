// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Pigweed HDLC/RPC framing facts from PixelBudsMacOS (MIT) and pbpctrl (Apache-2.0 OR MIT), as attributed by HeyBuds.
namespace EncoDeck.Protocol.Brands.Google;

/// <summary>One protobuf field value. Only the four wire types Maestro uses are accepted.</summary>
public abstract record ProtoValue
{
    public sealed record UInt(ulong Value) : ProtoValue;
    public sealed record Fixed32(uint Value) : ProtoValue;
    public sealed record Fixed64(ulong Value) : ProtoValue;
    public sealed record Bytes(byte[] Value) : ProtoValue;
}

/// <summary>
/// Bounded protobuf reader (16 KiB, 256 fields). A singular getter returns null when the field is
/// absent, repeated or of another wire type, so duplicates are never resolved by "last one wins".
/// </summary>
public sealed class ProtoFields
{
    private readonly Dictionary<int, List<ProtoValue>> _values;

    private ProtoFields(Dictionary<int, List<ProtoValue>> values) => _values = values;

    public static ProtoFields? TryParse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16_384) return null;
        var result = new Dictionary<int, List<ProtoValue>>();
        int offset = 0, count = 0;
        while (offset < bytes.Length)
        {
            if (!TryVarint(bytes, ref offset, out ulong key)) return null;
            ulong field = key >> 3;
            if (field == 0 || field > 536_870_911 || count >= 256) return null;
            count++;
            ProtoValue value;
            switch (key & 7)
            {
                case 0:
                    if (!TryVarint(bytes, ref offset, out ulong v)) return null;
                    value = new ProtoValue.UInt(v);
                    break;
                case 1:
                    if (bytes.Length - offset < 8) return null;
                    value = new ProtoValue.Fixed64(BitConverter.ToUInt64(bytes.Slice(offset, 8)));
                    offset += 8;
                    break;
                case 5:
                    if (bytes.Length - offset < 4) return null;
                    value = new ProtoValue.Fixed32(BitConverter.ToUInt32(bytes.Slice(offset, 4)));
                    offset += 4;
                    break;
                case 2:
                    if (!TryVarint(bytes, ref offset, out ulong n) || n > (ulong)(bytes.Length - offset)) return null;
                    value = new ProtoValue.Bytes(bytes.Slice(offset, (int)n).ToArray());
                    offset += (int)n;
                    break;
                default:
                    return null;
            }
            if (!result.TryGetValue((int)field, out var list)) result[(int)field] = list = [];
            list.Add(value);
        }
        return new ProtoFields(result);
    }

    public ProtoValue? One(int field) => _values.TryGetValue(field, out var list) && list.Count == 1 ? list[0] : null;

    public ulong? UInt(int field) => One(field) is ProtoValue.UInt v ? v.Value : null;

    public uint? Fixed(int field) => One(field) is ProtoValue.Fixed32 v ? v.Value : null;

    public byte[]? Bytes(int field) => One(field) is ProtoValue.Bytes v ? v.Value : null;

    public ProtoFields? Message(int field) => Bytes(field) is { } bytes ? TryParse(bytes) : null;

    public bool Present(int field) => _values.ContainsKey(field);

    private static bool TryVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong result)
    {
        result = 0;
        for (int i = 0; i < 10; i++)
        {
            if (offset >= bytes.Length) return false;
            byte b = bytes[offset++];
            // The tenth byte may only carry the 64th bit.
            if (i == 9 && b > 1) return false;
            result |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    public static byte[] Varint(ulong value)
    {
        var result = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            result.Add(value == 0 ? b : (byte)(b | 0x80));
        }
        while (value != 0);
        return [.. result];
    }

    public static byte[] Encode(int field, ProtoValue value)
    {
        (ulong wire, byte[] data) = value switch
        {
            ProtoValue.UInt v => (0UL, Varint(v.Value)),
            ProtoValue.Fixed32 v => (5UL, BitConverter.GetBytes(v.Value)),
            ProtoValue.Fixed64 v => (1UL, BitConverter.GetBytes(v.Value)),
            ProtoValue.Bytes v => (2UL, [.. Varint((ulong)v.Value.Length), .. v.Value]),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
        return [.. Varint(((ulong)field << 3) | wire), .. data];
    }
}

/// <summary>
/// One Pigweed RPC packet: type (0 request, 1 response, 4 client error, 5 server error,
/// 7 server stream), logical channel, service / method hashes, call id, status and payload.
/// </summary>
public sealed record GoogleRpcFrame(uint Type, uint Channel, uint Service, uint Method, uint Call, uint Status, byte[] Payload)
{
    public GoogleRpcFrame(uint channel, uint method, uint call, byte[]? payload = null, uint type = 0, uint status = 0)
        : this(type, channel, GoogleMaestro.ServiceId, method, call, status, payload ?? []) { }

    public byte[] Protobuf =>
    [
        .. ProtoFields.Encode(1, new ProtoValue.UInt(Type)), .. ProtoFields.Encode(2, new ProtoValue.UInt(Channel)),
        .. ProtoFields.Encode(3, new ProtoValue.Fixed32(Service)), .. ProtoFields.Encode(4, new ProtoValue.Fixed32(Method)),
        .. ProtoFields.Encode(5, new ProtoValue.Bytes(Payload)), .. ProtoFields.Encode(6, new ProtoValue.UInt(Status)),
        .. ProtoFields.Encode(7, new ProtoValue.UInt(Call)),
    ];

    /// <summary>Channel, service, method and call id are required; type, payload and status may be omitted (proto3 defaults).</summary>
    public static GoogleRpcFrame? Decode(ReadOnlySpan<byte> bytes)
    {
        if (ProtoFields.TryParse(bytes) is not { } p || p.UInt(2) is not { } channel || p.UInt(7) is not { } call
            || p.Fixed(3) is not { } service || p.Fixed(4) is not { } method || channel > uint.MaxValue || call > uint.MaxValue) return null;
        foreach (int key in new[] { 1, 5, 6 })
            if (p.Present(key) && p.One(key) is null) return null;
        if (p.Present(1) && p.UInt(1) is null || p.Present(5) && p.Bytes(5) is null || p.Present(6) && p.UInt(6) is null) return null;
        if ((p.UInt(1) ?? 0) > 8 || (p.UInt(6) ?? 0) > uint.MaxValue) return null;
        return new GoogleRpcFrame((uint)(p.UInt(1) ?? 0), (uint)channel, service, method, (uint)call, (uint)(p.UInt(6) ?? 0), p.Bytes(5) ?? []);
    }

    public bool Equals(GoogleRpcFrame? other) => other is not null && Type == other.Type && Channel == other.Channel
        && Service == other.Service && Method == other.Method && Call == other.Call && Status == other.Status
        && Payload.AsSpan().SequenceEqual(other.Payload);

    public override int GetHashCode() => HashCode.Combine(Type, Channel, Method, Call, Status, Payload.Length);
}

/// <summary>
/// Pigweed HDLC framing: <c>7E address… 03 protobuf crc32 7E</c>, with 7D escaping. Incremental
/// decoder; a bad CRC, invalid escape or an oversized frame drops only that frame.
/// </summary>
public sealed class GoogleFrameCodec
{
    private const int MaxFrame = 16_384;
    private readonly List<byte> _buffer = [];
    private bool _reading, _escaped;

    public void Reset()
    {
        _buffer.Clear();
        _reading = false;
        _escaped = false;
    }

    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>HDLC address of each documented logical RPC route (not an RFCOMM channel number).</summary>
    public static uint? Address(uint channel) => channel switch
    {
        19 => (10 << 6) | (3 << 10),
        21 => (10 << 6) | (4 << 10),
        24 => (13 << 6) | (3 << 10),
        26 => (13 << 6) | (4 << 10),
        _ => null,
    };

    public static byte[] Encode(GoogleRpcFrame frame)
    {
        if (Address(frame.Channel) is not { } address || frame.Payload.Length > 15_000)
            throw new ArgumentException("Unknown Maestro route or oversized payload.", nameof(frame));
        var raw = new List<byte>();
        do
        {
            byte part = (byte)((address & 0x7F) << 1);
            address >>= 7;
            raw.Add(address == 0 ? (byte)(part | 1) : part);
        }
        while (address != 0);
        raw.Add(3);
        raw.AddRange(frame.Protobuf);
        uint crc = Crc32(raw.ToArray());
        raw.AddRange(BitConverter.GetBytes(crc));
        var output = new List<byte>(raw.Count + 8) { 0x7E };
        foreach (byte b in raw)
        {
            if (b is 0x7E or 0x7D) output.AddRange([0x7D, (byte)(b ^ 0x20)]);
            else output.Add(b);
        }
        output.Add(0x7E);
        return [.. output];
    }

    public IReadOnlyList<GoogleRpcFrame> Feed(ReadOnlySpan<byte> data)
    {
        var frames = new List<GoogleRpcFrame>();
        foreach (byte b in data)
        {
            if (b == 0x7E)
            {
                if (_reading && !_escaped && DecodeFrame(_buffer.ToArray()) is { } frame) frames.Add(frame);
                _buffer.Clear();
                _reading = true;
                _escaped = false;
                continue;
            }
            if (!_reading) continue;
            if (_escaped)
            {
                if (b is not (0x5D or 0x5E))
                {
                    Reset();
                    continue;
                }
                _buffer.Add((byte)(b ^ 0x20));
                _escaped = false;
            }
            else if (b == 0x7D) _escaped = true;
            else _buffer.Add(b);
            if (_buffer.Count > MaxFrame) Reset();
        }
        return frames;
    }

    private static GoogleRpcFrame? DecodeFrame(byte[] bytes)
    {
        if (bytes.Length < 6) return null;
        int end = bytes.Length - 4;
        if (Crc32(bytes.AsSpan(0, end)) != BitConverter.ToUInt32(bytes, end)) return null;
        ulong address = 0;
        int offset = 0;
        for (int i = 0; i < 5; i++)
        {
            if (i >= end) return null;
            address |= (ulong)(bytes[i] >> 1) << (i * 7);
            if ((bytes[i] & 1) == 1)
            {
                offset = i + 1;
                break;
            }
        }
        if (offset == 0 || address > uint.MaxValue || offset >= end || bytes[offset] != 3) return null;
        return GoogleRpcFrame.Decode(bytes.AsSpan(offset + 1, end - offset - 1));
    }
}
