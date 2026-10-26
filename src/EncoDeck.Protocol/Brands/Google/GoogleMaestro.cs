// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Maestro service, setting ids and preset curves from pbpctrl (Apache-2.0 OR MIT) and PixelBudsMacOS (MIT),
// absent-battery-field semantics from MagicBuds (MIT), as attributed by HeyBuds.
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Google;

/// <summary>
/// The bounded Maestro subset: noise control (setting 13), five-band EQ (setting 16), software
/// version and runtime battery. No telemetry, account, OTA or undocumented settings.
/// </summary>
public static class GoogleMaestro
{
    public static readonly Guid ServiceUuid = new("25E97FF7-24CE-4C4C-8951-F764A708F7B5");

    /// <summary>Documented logical RPC routes, probed in this order. Not RFCOMM channel numbers.</summary>
    public static readonly uint[] Candidates = [19, 24, 21, 26];

    public static readonly uint ServiceId = Hash("maestro_pw.Maestro");

    public const ulong NoiseSetting = 13, EqualizerSetting = 16;

    /// <summary>Pigweed's 65599 name hash, used for service and method ids.</summary>
    public static uint Hash(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        uint hash = (uint)bytes.Length, coefficient = 65599;
        unchecked
        {
            foreach (byte b in bytes)
            {
                hash += coefficient * b;
                coefficient *= 65599;
            }
        }
        return hash;
    }

    /// <summary>ReadSetting / WriteSetting payload field 4, noise control field 13: 1 off, 2 ANC, 3 transparency, 4 adaptive.</summary>
    public static NoiseControlMode? Noise(ReadOnlySpan<byte> bytes) =>
        ProtoFields.TryParse(bytes)?.Message(4)?.UInt(13) switch
        {
            1 => NoiseControlMode.Off,
            2 => NoiseControlMode.NoiseCancellation,
            3 => NoiseControlMode.Transparency,
            4 => NoiseControlMode.Adaptive,
            _ => null,
        };

    public static byte[] NoiseWrite(NoiseControlMode mode)
    {
        ulong value = mode switch
        {
            NoiseControlMode.Off => 1,
            NoiseControlMode.NoiseCancellation => 2,
            NoiseControlMode.Transparency => 3,
            NoiseControlMode.Adaptive => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return ProtoFields.Encode(4, new ProtoValue.Bytes(ProtoFields.Encode(13, new ProtoValue.UInt(value))));
    }

    /// <summary>Balanced, heavy bass, vocal boost, treble boost and light bass five-band curves (dB).</summary>
    public static readonly float[][] Presets =
    [
        [0, 0, 0, 0, 0], [4, 3, 1, 0, 0], [-1, 0, 2, 3, 1], [0, 0, 1, 3, 4], [-4, -2, 0, 0, 0],
    ];

    /// <summary>
    /// EQ bands from setting 16. An explicitly present empty band message is a flat curve (proto3
    /// zero defaults); a band of another wire type, a duplicate or a value outside ±6 dB is invalid.
    /// </summary>
    public static float[]? Equalizer(ReadOnlySpan<byte> bytes)
    {
        if (ProtoFields.TryParse(bytes)?.Message(4)?.Message(16) is not { } p) return null;
        var result = new float[5];
        for (int field = 1; field <= 5; field++)
        {
            if (p.Present(field) && p.Fixed(field) is null) return null;
            float value = BitConverter.UInt32BitsToSingle(p.Fixed(field) ?? 0);
            if (!float.IsFinite(value) || value is < -6 or > 6) return null;
            result[field - 1] = value;
        }
        return result;
    }

    public static byte[] EqualizerWrite(IReadOnlyList<float> bands)
    {
        if (bands.Count != 5 || bands.Any(b => !float.IsFinite(b) || b is < -6 or > 6))
            throw new ArgumentException("Five finite bands within ±6 dB are required.", nameof(bands));
        byte[] eq = [.. bands.SelectMany((b, i) => ProtoFields.Encode(i + 1, new ProtoValue.Fixed32(BitConverter.SingleToUInt32Bits(b))))];
        return ProtoFields.Encode(4, new ProtoValue.Bytes(ProtoFields.Encode(16, new ProtoValue.Bytes(eq))));
    }

    /// <summary>The preset whose curve matches within 0.05 dB, or null for a custom curve.</summary>
    public static int? Preset(IReadOnlyList<float> bands)
    {
        if (bands.Count != 5) return null;
        int index = Array.FindIndex(Presets, p => p.Zip(bands).All(x => Math.Abs(x.First - x.Second) < 0.05f));
        return index >= 0 ? index : null;
    }

    /// <summary>GetSoftwareInfo: printable version strings of the case / left / right firmware, de-duplicated.</summary>
    public static string? Firmware(ReadOnlySpan<byte> bytes)
    {
        if (ProtoFields.TryParse(bytes)?.Message(4) is not { } firmware) return null;
        var versions = new SortedSet<string>(StringComparer.Ordinal);
        for (int field = 1; field <= 3; field++)
        {
            if (firmware.Message(field)?.Bytes(2) is not { Length: > 0 and <= 64 } raw || raw.Any(b => b is < 0x20 or > 0x7E)) continue;
            versions.Add(Encoding.ASCII.GetString(raw));
        }
        return versions.Count == 0 ? null : string.Join(" / ", versions);
    }

    /// <summary>
    /// RuntimeInfo field 6: case / left / right (1 / 2 / 3), each with level (1) and charging state
    /// (2: 1 discharging, 2 charging, 0 unknown). The report is a snapshot: an omitted component is
    /// unknown rather than kept from an earlier report. Returns null for a malformed or empty report.
    /// </summary>
    public static (BatteryReading? Case, BatteryReading? Left, BatteryReading? Right)? Runtime(ReadOnlySpan<byte> bytes)
    {
        if (ProtoFields.TryParse(bytes)?.Message(6) is not { } battery) return null;
        var readings = new BatteryReading?[3];
        bool any = false;
        for (int field = 1; field <= 3; field++)
        {
            if (!battery.Present(field)) continue;
            if (battery.Message(field) is not { } item || item.Present(1) && item.UInt(1) is null || item.Present(2) && item.UInt(2) is null) return null;
            ulong level = item.UInt(1) ?? 0, status = item.UInt(2) ?? 0;
            readings[field - 1] = level <= 100 && status is 1 or 2 ? new BatteryReading((int)level, status == 2) : null;
            any = true;
        }
        return any ? (readings[0], readings[1], readings[2]) : null;
    }
}
