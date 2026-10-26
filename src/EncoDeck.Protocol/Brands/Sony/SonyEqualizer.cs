// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>Official MDR table 1 EQ capability and parameter layouts (types 0, 2 and 4).</summary>
internal static class SonyEqualizer
{
    internal static bool TryPresets(ReadOnlySpan<byte> p, out IReadOnlyList<EqualizerPreset> presets, bool legacy = false)
    {
        presets = [];
        if (p.Length < 5 || p[0] != 0x51 || !(legacy ? p[1] is 1 or 3 : p[1] is 0 or 2 or 4)) return false;
        int count = p[4], offset = 5;
        var result = new List<EqualizerPreset>(); var ids = new HashSet<byte>();
        for (int i = 0; i < count; i++)
        {
            if (offset + 2 > p.Length) return false;
            byte id = p[offset]; int length = p[offset + 1];
            if (length > 128 || offset + 2 + length > p.Length || !ids.Add(id) || id == 255) return false;
            string name;
            try { name = new UTF8Encoding(false, true).GetString(p.Slice(offset + 2, length)); }
            catch (DecoderFallbackException) { return false; }
            if (name.Any(char.IsControl)) return false;
            // A name from the device wins; an unnamed known preset gets a localized label (some firmware,
            // e.g. WF-LC900 2.0.3 and WH-1000XM3, and every LE Audio link send all names empty).
            var label = string.IsNullOrWhiteSpace(name) ? SonySettings.PresetLabel(id, legacy) : null;
            result.Add(new(
                $"sony-{id}",
                label?.Name ?? (string.IsNullOrWhiteSpace(name) ? $"EQ {id}" : name),
                label?.Key ?? "",
                CapabilitySupport.Supported,
                EditablePreset(id)));
            offset += length + 2;
        }
        if (offset != p.Length || result.Count == 0) return false;
        presets = result; return true;
    }

    /// <summary>
    /// "Manual" and the two custom slots are the presets whose band values the headset lets us
    /// overwrite; the four sound presets are fixed and only selectable.
    /// </summary>
    internal static bool EditablePreset(byte id) => id is 0xA0 or 0xA1 or 0xA2;

    internal static bool TryParameter(ReadOnlySpan<byte> p, out byte preset, bool legacy = false) =>
        TryParameter(p, out preset, out _, legacy);

    /// <summary>
    /// Parses a parameter report. <paramref name="bands"/> holds the reported bands as dB offsets
    /// (raw byte 6 = 0 dB, one per step from 31 Hz to 16 kHz); it is empty when the device reported none.
    /// </summary>
    internal static bool TryParameter(ReadOnlySpan<byte> p, out byte preset, out int[] bands, bool legacy = false)
    {
        preset = 0; bands = [];
        if (p.Length < 4 || p[0] is not (0x57 or 0x59) || !(legacy ? p[1] is 1 or 3 : p[1] is 0 or 2 or 4) || p.Length != 4 + p[3] || p[2] == 255) return false;
        preset = p[2]; bands = ToDecibelOffsets(p.Slice(4)); return true;
    }

    /// <summary>Converts raw band bytes into dB offsets (-6..+6) and back.</summary>
    internal static int[] ToDecibelOffsets(ReadOnlySpan<byte> bands)
    {
        var result = new int[bands.Length];
        for (int i = 0; i < bands.Length; i++) result[i] = bands[i] - 6;
        return result;
    }

    internal static byte[] ToBandBytes(IReadOnlyList<int> bands)
    {
        var result = new byte[bands.Count];
        for (int i = 0; i < bands.Count; i++) result[i] = (byte)Math.Clamp(bands[i] + 6, 0, 12);
        return result;
    }
}
