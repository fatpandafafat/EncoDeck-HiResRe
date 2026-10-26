// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Length field corrected after OpenFreebuds (https://github.com/melianmiko/OpenFreebuds, MelianMiko and
// contributors, GPL-3.0) PR #116: the field is two bytes, big-endian.
namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>Wire format spoken by a HUAWEI audio device. Both share the same command numbers.</summary>
public enum HuaweiProtocol
{
    /// <summary>Classic FreeBuds framing: <c>5A len_hi len_lo 00 svc cmd TLV… crc_hi crc_lo</c>.</summary>
    Legacy,

    /// <summary>HarmonyOS "MBB" framing: <c>5A len_hi len_lo ctrl svc cmd TLV… crc_hi crc_lo</c>.</summary>
    Mbb,
}

/// <summary>One decoded HUAWEI frame. <see cref="Command"/> is <c>service &lt;&lt; 8 | command</c>; the payload is a TLV list.</summary>
public readonly record struct HuaweiFrame(ushort Command, byte[] Payload)
{
    public byte Service => (byte)(Command >> 8);

    public byte CommandId => (byte)Command;
}

/// <summary>Encodes requests and incrementally decodes a received byte stream (partial frames are buffered).</summary>
public interface IHuaweiFrameCodec
{
    HuaweiProtocol Protocol { get; }

    byte[] Encode(ushort command, ReadOnlySpan<byte> payload);

    /// <summary>Appends received bytes and returns every complete, CRC-valid frame.</summary>
    IReadOnlyList<HuaweiFrame> Feed(ReadOnlySpan<byte> data);
}

/// <summary>CRC and TLV helpers shared by both framings.</summary>
public static class HuaweiWire
{
    /// <summary>Largest TLV body accepted (matches the HeyBuds re-port's bound).</summary>
    public const int MaxPayload = 4096;

    /// <summary>CRC-16/XMODEM (poly 0x1021, init 0, no reflection), appended big-endian.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>Parses a <c>[type][len][value…]</c> list; a truncated trailing entry is ignored. Later duplicates win.</summary>
    public static Dictionary<byte, byte[]> ParseTlv(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<byte, byte[]>();
        int offset = 0;
        while (offset + 2 <= payload.Length)
        {
            byte type = payload[offset];
            int length = payload[offset + 1];
            int end = offset + 2 + length;
            if (end > payload.Length) break;
            fields[type] = payload[(offset + 2)..end].ToArray();
            offset = end;
        }
        return fields;
    }

    /// <summary>Strict parser for control responses; incomplete and duplicate fields cannot confirm a write.</summary>
    internal static bool TryParseTlv(ReadOnlySpan<byte> payload, out Dictionary<byte, byte[]> fields)
    {
        fields = [];
        while (!payload.IsEmpty)
        {
            if (payload.Length < 2 || payload.Length < payload[1] + 2) return false;
            int length = payload[1];
            if (!fields.TryAdd(payload[0], payload.Slice(2, length).ToArray())) return false;
            payload = payload[(2 + length)..];
        }
        return fields.Count > 0;
    }

    /// <summary>
    /// Reads the TLV 0x7F result code. Writes are answered with <c>7F 04 00 01 86 A0</c> (100000) when
    /// accepted; a shorter or longer value is kept as a nonzero error so it never passes as success.
    /// </summary>
    public static bool TryGetResultCode(Dictionary<byte, byte[]> fields, out uint code)
    {
        code = 0;
        if (!fields.TryGetValue(HuaweiCommands.ErrorTlv, out var value)) return false;
        code = value.Length == 4 ? (uint)(value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3]) : uint.MaxValue;
        return true;
    }

    /// <summary>A write acknowledgement (success result code): never a state report, whatever else it carries.</summary>
    public static bool IsSuccessAck(Dictionary<byte, byte[]> fields) =>
        TryGetResultCode(fields, out uint code) && code == HuaweiCommands.ResultSuccess;

    /// <summary>A result code other than success: the device refused the command.</summary>
    public static bool IsError(Dictionary<byte, byte[]> fields) =>
        TryGetResultCode(fields, out uint code) && code != HuaweiCommands.ResultSuccess;

    public static IHuaweiFrameCodec CreateCodec(HuaweiProtocol protocol) =>
        protocol == HuaweiProtocol.Mbb ? new MbbFrameCodec() : new LegacyHuaweiFrameCodec();

    internal static byte[] Finish(byte[] frame)
    {
        ushort crc = Crc16(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)crc;
        return frame;
    }

    internal static bool CrcMatches(List<byte> buffer, int total)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer)[..total];
        ushort crc = Crc16(span[..^2]);
        return span[^2] == (byte)(crc >> 8) && span[^1] == (byte)crc;
    }
}

/// <summary>
/// Classic FreeBuds framing. The length is two bytes, big-endian, and counts the zero byte, the
/// command and the TLVs (= TLV length + 3); the CRC covers everything from 0x5A to the last TLV byte.
/// Short frames read the same either way, but replies over 255 bytes (FreeBuds Pro 5 device info is
/// 282 bytes) need the high byte. There is no byte escaping in either HUAWEI framing.
/// </summary>
public sealed class LegacyHuaweiFrameCodec : IHuaweiFrameCodec
{
    private const int HeaderSize = 6; // 5A len len 00 svc cmd
    private readonly List<byte> _buffer = [];

    public HuaweiProtocol Protocol => HuaweiProtocol.Legacy;

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, HuaweiWire.MaxPayload);
        int length = payload.Length + 3;
        var frame = new byte[HeaderSize + payload.Length + 2];
        frame[0] = 0x5A;
        frame[1] = (byte)(length >> 8);
        frame[2] = (byte)length;
        frame[3] = 0x00;
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)command;
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return HuaweiWire.Finish(frame);
    }

    public IReadOnlyList<HuaweiFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<HuaweiFrame>();
        while (_buffer.Count > 0)
        {
            int start = _buffer.IndexOf(0x5A);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4) break;
            int length = (_buffer[1] << 8) | _buffer[2];
            if (_buffer[3] != 0x00 || length < 3 || length > HuaweiWire.MaxPayload + 3)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 5;
            if (_buffer.Count < total) break;
            if (!HuaweiWire.CrcMatches(_buffer, total))
            {
                _buffer.RemoveAt(0);
                continue;
            }
            var command = (ushort)((_buffer[4] << 8) | _buffer[5]);
            frames.Add(new HuaweiFrame(command, _buffer.GetRange(HeaderSize, length - 3).ToArray()));
            _buffer.RemoveRange(0, total);
        }
        if (_buffer.Count > 2 * (HuaweiWire.MaxPayload + 16)) _buffer.Clear();
        return frames;
    }
}

/// <summary>
/// HarmonyOS MBB framing: big-endian length (= TLV length + 3, excluding the CRC), a control byte
/// (0 = single frame, 1/2/3 = first/middle/last fragment) and the same CRC as the legacy framing.
/// Fragmented frames are skipped: their reassembly layout is unverified upstream, so they are never
/// interpreted as control state.
/// </summary>
public sealed class MbbFrameCodec : IHuaweiFrameCodec
{
    private const int HeaderSize = 6; // 5A len len ctrl svc cmd
    private readonly List<byte> _buffer = [];

    public HuaweiProtocol Protocol => HuaweiProtocol.Mbb;

    /// <summary>Number of fragment frames received and dropped (diagnostics).</summary>
    public int DroppedFragments { get; private set; }

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, HuaweiWire.MaxPayload);
        int length = payload.Length + 3;
        var frame = new byte[length + 5];
        frame[0] = 0x5A;
        frame[1] = (byte)(length >> 8);
        frame[2] = (byte)length;
        frame[3] = 0x00;
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)command;
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return HuaweiWire.Finish(frame);
    }

    public IReadOnlyList<HuaweiFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<HuaweiFrame>();
        while (_buffer.Count > 0)
        {
            int start = _buffer.IndexOf(0x5A);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4) break;
            int length = (_buffer[1] << 8) | _buffer[2];
            byte control = _buffer[3];
            if (length < 3 || length > HuaweiWire.MaxPayload + 3 || control > 3)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 5;
            if (_buffer.Count < total) break;
            if (!HuaweiWire.CrcMatches(_buffer, total))
            {
                _buffer.RemoveAt(0);
                continue;
            }
            if (control == 0)
            {
                var command = (ushort)((_buffer[4] << 8) | _buffer[5]);
                frames.Add(new HuaweiFrame(command, _buffer.GetRange(HeaderSize, length - 3).ToArray()));
            }
            else
            {
                DroppedFragments++;
            }
            _buffer.RemoveRange(0, total);
        }
        if (_buffer.Count > 2 * (HuaweiWire.MaxPayload + 16)) _buffer.Clear();
        return frames;
    }
}
