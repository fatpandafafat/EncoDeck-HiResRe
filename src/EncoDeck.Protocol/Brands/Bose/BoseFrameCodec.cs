// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// BMAP protocol facts from bosectl and bozo (MIT) as attributed by HeyBuds.
using System.Text;

namespace EncoDeck.Protocol.Brands.Bose;

/// <summary>BMAP operator, the low nibble of the third header byte.</summary>
public enum BoseOperation : byte
{
    Set = 0,
    Get = 1,
    SetGet = 2,
    Status = 3,
    Error = 4,
    Start = 5,
    Result = 6,
    Processing = 7,
}

/// <summary>One BMAP message: function block, function, operator, port/route nibble and payload.</summary>
public sealed record BoseFrame(byte Block, byte Function, BoseOperation Operation, byte[] Payload, byte Route = 0)
{
    /// <summary>Same function on the host route; other routes are never a reply to this host.</summary>
    public bool Matches(byte block, byte function) => Block == block && Function == function && Route == 0;
}

/// <summary>
/// BMAP framing over RFCOMM: <c>block function (route|operator) length payload…</c>. There is no
/// sync byte or checksum, so a corrupted stream is retired instead of being scanned for a
/// plausible command in arbitrary data.
/// </summary>
public static class BoseFrameCodec
{
    public const int MaxPayload = 255;

    public static byte[] Encode(BoseFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frame.Payload.Length, MaxPayload);
        if ((frame.Route & 0x0F) != 0) throw new ArgumentException("The route occupies the high nibble only.", nameof(frame));
        return [frame.Block, frame.Function, (byte)(frame.Route | (byte)frame.Operation), (byte)frame.Payload.Length, .. frame.Payload];
    }
}

/// <summary>Incremental BMAP decoder. Retained storage is bounded to one 259-byte frame.</summary>
public sealed class BoseFrameDecoder
{
    private const int MaxChunk = 16_384;
    private readonly List<byte> _buffer = [];

    /// <summary>True after an unknown operator: the stream position can no longer be trusted.</summary>
    public bool Failed { get; private set; }

    public void Reset()
    {
        _buffer.Clear();
        Failed = false;
    }

    public IReadOnlyList<BoseFrame> Feed(ReadOnlySpan<byte> data)
    {
        if (Failed) return [];
        if (data.Length > MaxChunk)
        {
            _buffer.Clear();
            Failed = true;
            return [];
        }
        var frames = new List<BoseFrame>();
        foreach (byte b in data)
        {
            _buffer.Add(b);
            if (_buffer.Count < 4) continue;
            int op = _buffer[2] & 0x0F;
            if (op > (int)BoseOperation.Processing)
            {
                _buffer.Clear();
                Failed = true;
                return [];
            }
            if (_buffer.Count == 4 + _buffer[3])
            {
                frames.Add(new BoseFrame(_buffer[0], _buffer[1], (BoseOperation)op, _buffer.GetRange(4, _buffer[3]).ToArray(), (byte)(_buffer[2] & 0xF0)));
                _buffer.Clear();
            }
        }
        return frames;
    }
}

/// <summary>One battery component; its location is not inferred from the id.</summary>
public sealed record BoseBatteryComponent(byte Id, int? Percent);

/// <summary>A mode (Quiet, Aware or a user mode) from the mode configuration GET [31.6].</summary>
public sealed record BoseModeConfig(byte Index, ushort Prompt, string Name, bool Editable, bool Configured, byte Cnc, byte[] Raw);

/// <summary>Bounded parsers for the BMAP payloads this adapter reads.</summary>
public static class BosePayload
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>[percent, reserved, reserved, component id] per component; 255 = unknown percent.</summary>
    public static IReadOnlyList<BoseBatteryComponent>? Battery(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 32 || bytes.Length % 4 != 0) return null;
        var seen = new HashSet<byte>();
        var result = new List<BoseBatteryComponent>();
        for (int offset = 0; offset < bytes.Length; offset += 4)
        {
            byte id = bytes[offset + 3], value = bytes[offset];
            if (!seen.Add(id)) return null;
            result.Add(new BoseBatteryComponent(id, value <= 100 ? value : null));
        }
        return result;
    }

    /// <summary>
    /// Favorites GET [31.8]: count + 16-bit mask. Acknowledgements and truncated or unrecognised
    /// layouts never establish a mode capability.
    /// </summary>
    public static int? ModeCount(ReadOnlySpan<byte> bytes, BoseModel model)
    {
        if (bytes.Length != 3) return null;
        int count = bytes[0];
        if (count < 2 || count > model.MaximumModeCount()) return null;
        int mask = (bytes[1] << 8) | bytes[2];
        return mask >> count == 0 ? count : null;
    }

    /// <summary>Full mode status; a short SET echo or another generation's layout is rejected.</summary>
    public static BoseModeConfig? Mode(ReadOnlySpan<byte> bytes, BoseModel model)
    {
        if (bytes.Length != model.ModeStatusLength() || bytes[0] >= model.MaximumModeCount()
            || bytes[3] > 1 || bytes[4] > 1 || bytes[42] > 10) return null;
        var rawName = bytes[6..38];
        int end = rawName.IndexOf((byte)0);
        string name;
        try
        {
            name = StrictUtf8.GetString(end < 0 ? rawName : rawName[..end]);
        }
        catch (ArgumentException)
        {
            return null;
        }
        return new BoseModeConfig(bytes[0], (ushort)((bytes[1] << 8) | bytes[2]), name, bytes[3] == 1, bytes[4] == 1, bytes[42], bytes.ToArray());
    }

    /// <summary>Only the two fixed presets map to EncoDeck modes; user modes stay unknown.</summary>
    public static Core.State.NoiseControlMode? Noise(byte? index, IReadOnlyDictionary<byte, BoseModeConfig> modes)
    {
        if (index is not { } i || !modes.TryGetValue(i, out var mode) || mode.Editable) return null;
        if (i == 0 && mode.Prompt == 1) return Core.State.NoiseControlMode.NoiseCancellation;
        if (i == 1 && mode.Prompt == 2) return Core.State.NoiseControlMode.Transparency;
        return null;
    }

    /// <summary>Product info GET [0.5] when it reads like a dotted printable version string.</summary>
    public static string? Firmware(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 128) return null;
        foreach (byte b in bytes)
            if (b is < 32 or >= 127) return null;
        string version = Encoding.ASCII.GetString(bytes);
        return version.Contains('.', StringComparison.Ordinal) ? version : null;
    }
}
