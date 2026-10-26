// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Framing facts from swift-nothing-ear, nothing-x-macos (GPL-3.0) and openNothing (protocol facts only), as attributed by HeyBuds.
namespace EncoDeck.Protocol.Brands.Nothing;

/// <summary>One Nothing / CMF frame: 16-bit command, one-byte operation id (sequence) and payload.</summary>
public sealed record NothingFrame(ushort Command, byte Sequence, byte[] Payload);

/// <summary>
/// A read (with <see cref="ResponseCommand"/>) or a write (without). A write is never confirmed by
/// its acknowledgement: the setting must be read again with a new sequence and parsed.
/// </summary>
public sealed record NothingRequest(ushort Command, byte[] Payload, ushort? ResponseCommand)
{
    public NothingRequest(ushort command, ushort? responseCommand = null) : this(command, [], responseCommand)
    {
    }

    /// <summary>Correlates on both command and sequence; an ACK or event never matches.</summary>
    public bool Matches(NothingFrame frame, byte sequence) => ResponseCommand == frame.Command && frame.Sequence == sequence;
}

/// <summary>
/// Nothing RFCOMM framing: <c>55 60 01 cmdLo cmdHi lenLo lenHi seq payload… crcLo crcHi</c>.
/// The CRC is reflected CRC-16 (polynomial A001, initial FFFF, no final XOR) over header and payload.
/// </summary>
public static class NothingFrameCodec
{
    public const int MaxPayload = 1024;

    public static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        ushort value = 0xFFFF;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int i = 0; i < 8; i++) value = (value & 1) == 0 ? (ushort)(value >> 1) : (ushort)((value >> 1) ^ 0xA001);
        }
        return value;
    }

    public static byte[] Encode(ushort command, byte sequence, ReadOnlySpan<byte> payload, int maxPayload = MaxPayload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, Math.Min(maxPayload, MaxPayload));
        var frame = new byte[payload.Length + 10];
        frame[0] = 0x55;
        frame[1] = 0x60;
        frame[2] = 1;
        frame[3] = (byte)command;
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)payload.Length;
        frame[6] = (byte)(payload.Length >> 8);
        frame[7] = sequence;
        payload.CopyTo(frame.AsSpan(8));
        ushort crc = Checksum(frame.AsSpan(0, payload.Length + 8));
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    public static byte[] Encode(NothingRequest request, byte sequence) => Encode(request.Command, sequence, request.Payload);
}

/// <summary>
/// Incremental receiver bound to one model: it accepts only that model's response commands and its
/// fixed CRC region, never trying the other region or a missing checksum. Retains at most one
/// maximum-size frame; reset it at a session boundary or after an abandoned (timed-out) reply.
/// </summary>
public sealed class NothingFrameDecoder(NothingModel model, int maxPayload = NothingFrameCodec.MaxPayload)
{
    private readonly int _maxPayload = Math.Clamp(maxPayload, 0, NothingFrameCodec.MaxPayload);
    private readonly List<byte> _buffer = [];

    public int BufferedCount => _buffer.Count;

    public void Reset() => _buffer.Clear();

    public IReadOnlyList<NothingFrame> Feed(ReadOnlySpan<byte> data)
    {
        var frames = new List<NothingFrame>();
        foreach (byte b in data)
        {
            _buffer.Add(b);
            while (_buffer.Count > 0)
            {
                if (_buffer[0] != 0x55)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }
                if (_buffer.Count < 8) break;
                ushort command = (ushort)(_buffer[3] | _buffer[4] << 8);
                int length = _buffer[5] | _buffer[6] << 8;
                bool noCrc = _buffer[1] == 0 && _buffer[2] == 1 && model.IsCrcFreeEvent(command, _buffer[7]);
                if (length > _maxPayload || !model.Accepts(command) || !(noCrc || _buffer[1] == 0x60 && _buffer[2] == 1))
                {
                    _buffer.RemoveAt(0);
                    continue;
                }
                int end = 8 + length, total = end + (noCrc ? 0 : 2);
                if (_buffer.Count < total) break;
                var bytes = _buffer.GetRange(0, total).ToArray();
                if (!noCrc)
                {
                    int start = model.PayloadOnlyCrc ? 8 : 0;
                    ushort expected = (ushort)(bytes[end] | bytes[end + 1] << 8);
                    if (NothingFrameCodec.Checksum(bytes.AsSpan(start, end - start)) != expected)
                    {
                        _buffer.RemoveAt(0);
                        continue;
                    }
                }
                frames.Add(new NothingFrame(command, bytes[7], bytes[8..end]));
                _buffer.RemoveRange(0, total);
            }
        }
        return frames;
    }
}
