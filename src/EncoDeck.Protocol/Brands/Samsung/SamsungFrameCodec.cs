// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Samsung SPP framing facts from Budsy (MIT) and GalaxyBudsClient (GPL-3.0), as attributed by HeyBuds.
namespace EncoDeck.Protocol.Brands.Samsung;

/// <summary>One decoded Galaxy Buds SPP message: message id, payload and the response flag.</summary>
public sealed record SamsungFrame(byte Command, byte[] Payload, bool Response = false)
{
    public bool Equals(SamsungFrame? other) =>
        other is not null && Command == other.Command && Response == other.Response && Payload.AsSpan().SequenceEqual(other.Payload);

    public override int GetHashCode() => HashCode.Combine(Command, Response, Payload.Length);
}

/// <summary>
/// Modern Galaxy Buds SPP framing: <c>FD lenLo lenHi id payload… crcLo crcHi DD</c>. The 10-bit
/// length counts id + payload + CRC; bit 12 marks a response. CRC-16/XMODEM covers id and payload.
/// </summary>
public static class SamsungFrameCodec
{
    public const byte Start = 0xFD;
    public const byte End = 0xDD;

    /// <summary>Largest payload the 10-bit length field can carry (1023 - id - CRC).</summary>
    public const int MaxPayload = 1020;

    public static byte[] Encode(SamsungFrame frame) => Encode(frame.Command, frame.Payload, frame.Response);

    public static byte[] Encode(byte command, ReadOnlySpan<byte> payload, bool response = false)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayload);
        int length = (payload.Length + 3) | (response ? 0x1000 : 0);
        var frame = new byte[payload.Length + 7];
        frame[0] = Start;
        frame[1] = (byte)length;
        frame[2] = (byte)(length >> 8);
        frame[3] = command;
        payload.CopyTo(frame.AsSpan(4));
        ushort crc = Checksum(frame.AsSpan(3, payload.Length + 1));
        frame[^3] = (byte)crc;
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = End;
        return frame;
    }

    /// <summary>CRC-16/XMODEM (polynomial 1021, initial 0, no final XOR).</summary>
    public static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}

/// <summary>
/// Incremental decoder. Fragmented firmware, SMEP and legacy (<c>FE … EE</c>) messages are outside
/// this backend: their reserved header bits make the start byte resynchronize instead.
/// </summary>
public sealed class SamsungFrameDecoder
{
    private const int MaxBuffered = 2048;
    private readonly List<byte> _buffer = [];

    public void Reset() => _buffer.Clear();

    public IReadOnlyList<SamsungFrame> Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) _buffer.Add(b);
        var frames = new List<SamsungFrame>();
        while (_buffer.Count > 0)
        {
            if (_buffer[0] != SamsungFrameCodec.Start)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            if (_buffer.Count < 3) break;
            int header = _buffer[1] | (_buffer[2] << 8), length = header & 0x3FF;
            if (length < 3 || (header & 0xEC00) != 0)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 4;
            if (_buffer.Count < total) break;
            byte[] body = _buffer.GetRange(3, length - 2).ToArray();
            ushort crc = (ushort)(_buffer[total - 3] | (_buffer[total - 2] << 8));
            if (_buffer[total - 1] != SamsungFrameCodec.End || SamsungFrameCodec.Checksum(body) != crc)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            frames.Add(new SamsungFrame(body[0], body[1..], (header & 0x1000) != 0));
            _buffer.RemoveRange(0, total);
        }
        if (_buffer.Count > MaxBuffered) _buffer.Clear();
        return frames;
    }
}
