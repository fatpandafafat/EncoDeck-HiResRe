// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Request/response/notify roles from BudsLink (https://github.com/maniacx/BudsLink, GPL-3.0) and
// redmi-buds-bar by robin-liquidium (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0).
namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// One Xiaomi RCSP frame:
/// <c>FE DC BA | kind | opCode | length (2, big endian, = 1 + payload) | sequence | payload | EF</c>.
/// Kind bit 7 marks a request (0 = response), bit 6 asks for a reply. On the wire a response puts
/// its status byte where a request has the sequence; <see cref="XiaomiConfigCodec.NormalizeResponse"/>
/// moves it to <see cref="Status"/>.
/// </summary>
/// <param name="Kind">Type byte (0xC4 phone requests, 0xC0/0xC7/0x87 earbud requests, 0x04 responses).</param>
/// <param name="OpCode">RCSP command: 0x02 device info, 0x50/0x51 auth, 0xF2 set config, 0xF3 get config, 0xF4 config push.</param>
/// <param name="Sequence">Request sequence byte, echoed by the earbuds.</param>
/// <param name="Payload">Frame payload.</param>
public sealed record XiaomiFrame(byte Kind, byte OpCode, byte Sequence, byte[] Payload)
{
    /// <summary>Response status of a normalized response (0 = success); always 0 for requests.</summary>
    public byte Status { get; init; }

    /// <summary>Kind bit 7 clear: an answer to a request.</summary>
    public bool IsResponse => (Kind & 0x80) == 0;

    /// <summary>A request (from either side) that asks for a reply.</summary>
    public bool NeedsReply => (Kind & 0xC0) == 0xC0;

    /// <summary>The wire form of a response: status where the sequence goes, then the echoed sequence.</summary>
    public static XiaomiFrame Response(byte kind, byte opCode, byte sequence, byte[] data, byte status = 0) =>
        new(kind, opCode, status, [sequence, .. data]);

    public const byte Header0 = 0xFE;
    public const byte Header1 = 0xDC;
    public const byte Header2 = 0xBA;
    public const byte Footer = 0xEF;

    /// <summary>Header (3) + kind + opCode + length (2) + sequence + footer.</summary>
    public const int Overhead = 9;

    /// <summary>Largest payload accepted from the wire; anything bigger is treated as noise.</summary>
    public const int MaxPayload = 4096;

    public byte[] Encode()
    {
        var frame = new byte[Overhead + Payload.Length];
        frame[0] = Header0;
        frame[1] = Header1;
        frame[2] = Header2;
        frame[3] = Kind;
        frame[4] = OpCode;
        int length = 1 + Payload.Length;
        frame[5] = (byte)(length >> 8);
        frame[6] = (byte)length;
        frame[7] = Sequence;
        Payload.CopyTo(frame.AsSpan(8));
        frame[^1] = Footer;
        return frame;
    }

    public override string ToString() => $"kind=0x{Kind:X2} op=0x{OpCode:X2} seq=0x{Sequence:X2} status=0x{Status:X2} payload={Convert.ToHexString(Payload)}";
}

/// <summary>Reassembles <see cref="XiaomiFrame"/>s from a byte stream that may split or merge frames.</summary>
public sealed class XiaomiFrameDecoder
{
    private readonly List<byte> _buffer = [];

    /// <summary>Appends <paramref name="data"/> and returns every complete frame found so far.</summary>
    public IReadOnlyList<XiaomiFrame> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<XiaomiFrame>();
        while (true)
        {
            int start = FindHeader();
            if (start < 0)
            {
                // Keep a possible partial header at the end.
                int keep = Math.Min(_buffer.Count, 2);
                _buffer.RemoveRange(0, _buffer.Count - keep);
                if (keep > 0 && _buffer[0] != XiaomiFrame.Header0) _buffer.RemoveAt(0);
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 8) break;

            int length = (_buffer[5] << 8) | _buffer[6];
            if (length < 1 || length - 1 > XiaomiFrame.MaxPayload)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = length + 8;
            if (_buffer.Count < total) break;
            if (_buffer[total - 1] != XiaomiFrame.Footer)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var payload = _buffer.GetRange(8, length - 1).ToArray();
            frames.Add(new XiaomiFrame(_buffer[3], _buffer[4], _buffer[7], payload));
            _buffer.RemoveRange(0, total);
        }
        return frames;
    }

    private int FindHeader()
    {
        for (int i = 0; i + 2 < _buffer.Count; i++)
        {
            if (_buffer[i] == XiaomiFrame.Header0 && _buffer[i + 1] == XiaomiFrame.Header1 && _buffer[i + 2] == XiaomiFrame.Header2) return i;
        }
        return -1;
    }
}
