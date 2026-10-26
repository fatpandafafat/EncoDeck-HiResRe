// SPDX-License-Identifier: GPL-3.0-only
// Record layout cross-checked against BudsLink (https://github.com/maniacx/BudsLink, GPL-3.0) and
// redmi-buds-bar by robin-liquidium (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0).
namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>RCSP CommonConfig records: length (including two type bytes), type BE, value.</summary>
public static class XiaomiConfigCodec
{
    /// <summary>A get-config request: a raw list of BE16 ids (no record length).</summary>
    public static byte[] ReadPayload(params ushort[] ids) => ids.SelectMany(id => new byte[] { (byte)(id >> 8), (byte)id }).ToArray();

    public static byte[] WritePayload(ushort id, ReadOnlySpan<byte> value)
    {
        if (value.Length > 252) throw new ArgumentOutOfRangeException(nameof(value));
        return [(byte)(value.Length + 2), (byte)(id >> 8), (byte)id, .. value];
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out IReadOnlyDictionary<ushort, byte[]> values)
    {
        var result = new Dictionary<ushort, byte[]>(); values = result;
        while (!payload.IsEmpty)
        {
            int length = payload[0];
            if (length < 2 || length == 255 || payload.Length < length + 1) return false;
            ushort id = (ushort)((payload[1] << 8) | payload[2]);
            // Duplicate records are ambiguous; never use a partial/invalid packet to enable a feature.
            if (!result.TryAdd(id, payload.Slice(3, length - 2).ToArray())) return false;
            payload = payload[(length + 1)..];
        }
        return result.Count > 0;
    }

    /// <summary>
    /// Turns a wire response (status where a request has its sequence, then the sequence) into a
    /// frame with the real <see cref="XiaomiFrame.Sequence"/> and <see cref="XiaomiFrame.Status"/>.
    /// Requests pass through; a response without a sequence byte is dropped (null).
    /// </summary>
    public static XiaomiFrame? NormalizeResponse(XiaomiFrame frame)
    {
        if (!frame.IsResponse) return frame;
        if (frame.Payload.Length == 0) return null;
        return frame with { Sequence = frame.Payload[0], Payload = frame.Payload[1..], Status = frame.Sequence };
    }
}
