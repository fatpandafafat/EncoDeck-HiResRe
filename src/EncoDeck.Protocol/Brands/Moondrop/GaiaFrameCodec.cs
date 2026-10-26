// SPDX-License-Identifier: GPL-3.0-only
// Adapted from huime180/MiuixMoondrop, GPL-3.0-or-later.
using System.Buffers.Binary;

namespace EncoDeck.Protocol.Brands.Moondrop;

public sealed record GaiaFrame(int Feature, int Type, int Command, byte[] Payload);

/// <summary>GAIA QTiL PDU and length-delimited RFCOMM transport. BLE delivers one PDU per notification.</summary>
public static class GaiaFrameCodec
{
    public static byte[] Encode(int feature, int command, ReadOnlySpan<byte> payload, bool rfcomm)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)feature, 127u);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)command, 127u);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, 65535);
        int header = rfcomm ? (payload.Length > 255 ? 5 : 4) : 0;
        var result = new byte[header + 4 + payload.Length];
        if (rfcomm)
        {
            result[0] = 0xFF;
            result[1] = 4;
            result[2] = header == 5 ? (byte)2 : (byte)0;
            if (header == 5) BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3), (ushort)payload.Length);
            else result[3] = (byte)payload.Length;
        }
        result[header + 1] = 0x1D;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(header + 2), (ushort)((feature << 9) | command));
        payload.CopyTo(result.AsSpan(header + 4));
        return result;
    }
    public static byte[] EncodeVersionProbe(bool rfcomm) => rfcomm
        ? [0xFF, 4, 0, 0, 0, 0x0A, 3, 0] : [0, 0x0A, 3, 0];


    public static GaiaFrame? Decode(ReadOnlySpan<byte> pdu)
    {
        // Legacy CSR GET_API_VERSION is the upstream RFCOMM framing probe, not a QTiL feature.
        if (pdu.Length == 8 && pdu[0] == 0 && pdu[1] == 0x0A && pdu[2] == 0x83 && pdu[3] == 0 && pdu[4] == 0)
            return new(-1, 2, 0, pdu[4..].ToArray());
        if (pdu.Length < 4 || pdu[0] != 0 || pdu[1] != 0x1D) return null;
        int value = BinaryPrimitives.ReadUInt16BigEndian(pdu[2..]);
        // Type 3 is an error reply; its payload is the status code.
        return new GaiaFrame(value >> 9, (value >> 7) & 3, value & 0x7F, pdu[4..].ToArray());
    }
}

/// <summary>Framed streams are length-delimited. Bare streams use known response sizes, otherwise an explicit burst end; they have no universal packet boundary.</summary>
public sealed class GaiaFrameDecoder
{
    private readonly List<byte> _buffer = [];

    public void Reset() => _buffer.Clear();

    public IReadOnlyList<GaiaFrame> Feed(ReadOnlySpan<byte> data, bool rfcomm, bool burstEnd = true)
    {
        if (!rfcomm)
        {
            var frame = GaiaFrameCodec.Decode(data);
            return frame is null ? [] : [frame];
        }
        foreach (byte b in data) _buffer.Add(b);
        var frames = new List<GaiaFrame>();
        int consumed = 0;
        while (_buffer.Count - consumed >= 4)
        {
            if (_buffer[consumed] == 0 && _buffer[consumed + 1] is 0x1D or 0x0A)
            {
                int feature = _buffer[consumed + 2] >> 1;
                int command = _buffer[consumed + 3] & 0x7F;
                bool error = (_buffer[consumed + 2] & 1) != 0 && (_buffer[consumed + 3] & 0x80) != 0;
                // Error replies carry a status code instead of the known response body.
                int payloadLength = _buffer[consumed + 1] == 0x0A ? 4 : error ? -1 : (feature, command) switch
                {
                    (14, 1) => 3,
                    (22, 2) => 5,
                    (2, 1) or (8 or 32, 3) or (15 or 19 or 20, 1) or (16, 5) or (5, 0 or 2 or 4) => 1,
                    _ => -1,
                };
                // SET acknowledgements and variable-length discovery/battery payloads require a quiet burst.
                int size = payloadLength < 0 ? burstEnd ? _buffer.Count - consumed : -1 : 4 + payloadLength;
                if (size < 0 || _buffer.Count - consumed < size) break;
                var raw = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer).Slice(consumed, size);
                if (GaiaFrameCodec.Decode(raw) is { } decoded) frames.Add(decoded);
                consumed += size;
                continue;
            }
            if (_buffer[consumed] != 0xFF || _buffer[consumed + 1] is not (3 or 4))
            {
                consumed++;
                continue;
            }
            int flags = _buffer[consumed + 2];
            int header = _buffer[consumed + 1] >= 4 && (flags & 2) != 0 ? 5 : 4;
            if (_buffer.Count - consumed < header) break;
            int length = header == 5 ? (_buffer[consumed + 3] << 8) | _buffer[consumed + 4] : _buffer[consumed + 3];
            int total = header + 4 + length + (flags & 1);
            if (_buffer.Count - consumed < total) break;
            byte checksum = 0;
            if ((flags & 1) != 0)
                for (int i = 0; i < total; i++) checksum ^= _buffer[consumed + i];
            if (checksum == 0)
            {
                var pdu = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer).Slice(consumed + header, 4 + length);
                if (GaiaFrameCodec.Decode(pdu) is { } frame) frames.Add(frame);
            }
            consumed += total;
        }
        if (consumed > 0) _buffer.RemoveRange(0, consumed);
        return frames;
    }
}
