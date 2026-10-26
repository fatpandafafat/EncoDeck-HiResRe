// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// GATT framing (no FF header) from vivopods-mac (https://github.com/FxRayHughes/vivopods-mac, be25133, GPL-3.0-only).
using System.Buffers.Binary;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>
/// One vivo compact GAIA frame:
/// <c>FF [version] [flags=00] [payloadLen] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload…]</c>.
/// </summary>
public sealed record VivoFrame(ushort Command, byte[] Payload)
{
    /// <summary>
    /// Encodes the frame. The handshake uses the GAIA vendor and version 4, the battery query is
    /// always version 4, every other command uses the model's GAIA version and the vivo vendor.
    /// </summary>
    public byte[] Encode(int gaiaVersion)
    {
        if (Payload.Length > VivoConstants.MaxPayload) throw new ArgumentException("Payload too long.", nameof(Payload));
        bool handshake = Command == VivoCommands.Handshake;
        bool battery = Command is VivoCommands.QueryBattery or VivoCommands.ReportBattery;
        var frame = new byte[VivoConstants.HeaderLength + Payload.Length];
        frame[0] = VivoConstants.Preamble;
        frame[1] = handshake || battery ? VivoConstants.HandshakeVersion : (byte)gaiaVersion;
        frame[2] = 0;
        frame[3] = (byte)Payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), handshake ? VivoConstants.GaiaVendor : VivoConstants.VivoVendor);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), Command);
        Payload.CopyTo(frame.AsSpan(VivoConstants.HeaderLength));
        return frame;
    }

    /// <summary>
    /// GATT form: <c>[vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload…]</c>, the RFCOMM frame
    /// without its <c>FF version flags length</c> header (each GATT message carries its own length).
    /// </summary>
    public byte[] EncodeGatt(int gaiaVersion) => Encode(gaiaVersion)[4..];

    /// <summary>Decodes one GATT message; null when it is too short or names another vendor.</summary>
    public static VivoFrame? DecodeGatt(ReadOnlySpan<byte> message)
    {
        if (message.Length < 4 || message.Length - 4 > VivoConstants.MaxPayload) return null;
        ushort vendor = BinaryPrimitives.ReadUInt16BigEndian(message);
        if (vendor is not (VivoConstants.GaiaVendor or VivoConstants.VivoVendor)) return null;
        return new VivoFrame(BinaryPrimitives.ReadUInt16BigEndian(message[2..]), message[4..].ToArray());
    }

    public override string ToString() => $"0x{Command:X4} [{Convert.ToHexString(Payload)}]";
}

/// <summary>
/// Incremental decoder: buffers partial frames across reads and resynchronizes on garbage by
/// dropping one byte at a time until a plausible header (version 3/4, flags 0, known vendor) is found.
/// </summary>
public sealed class VivoFrameDecoder
{
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<VivoFrame> Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) _buffer.Add(b);
        var frames = new List<VivoFrame>();
        while (_buffer.Count > 0)
        {
            if (_buffer[0] != VivoConstants.Preamble)
            {
                int next = _buffer.IndexOf(VivoConstants.Preamble);
                if (next < 0)
                {
                    _buffer.Clear();
                    break;
                }
                _buffer.RemoveRange(0, next);
            }
            if (_buffer.Count < VivoConstants.HeaderLength) break;
            ushort vendor = (ushort)((_buffer[4] << 8) | _buffer[5]);
            bool plausible = _buffer[1] is 3 or 4
                && _buffer[2] == 0
                && _buffer[3] <= VivoConstants.MaxPayload
                && vendor is VivoConstants.GaiaVendor or VivoConstants.VivoVendor;
            if (!plausible)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            int total = VivoConstants.HeaderLength + _buffer[3];
            if (_buffer.Count < total) break;
            ushort command = (ushort)((_buffer[6] << 8) | _buffer[7]);
            byte[] payload = _buffer.GetRange(VivoConstants.HeaderLength, _buffer[3]).ToArray();
            _buffer.RemoveRange(0, total);
            frames.Add(new VivoFrame(command, payload));
        }
        return frames;
    }
}
