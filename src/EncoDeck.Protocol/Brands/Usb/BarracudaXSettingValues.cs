// SPDX-License-Identifier: GPL-3.0-only
// Preset ids, band layout and idle choices follow nightdevx/barracuda-x-battery barracuda.py (MIT,
// Copyright (c) 2026 nightdijital), tested on a 1532:0550 receiver; see THIRD_PARTY_NOTICES.md.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Profiles;

namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>How the Barracuda X (1532:0550) register values map to EncoDeck's EQ, switch and picker state.</summary>
public static class BarracudaXSettingValues
{
    /// <summary>Settings offered in the UI, in read order.</summary>
    public static IReadOnlyList<RazerPaSetting> Offered { get; } =
        [RazerPaSetting.EqPreset, RazerPaSetting.CustomEq, RazerPaSetting.DoNotDisturb, RazerPaSetting.AutoOff];

    /// <summary>The single custom slot (preset FF) is shown as a custom EQ with this id.</summary>
    public const int CustomPresetId = 0xFF;

    /// <summary>Raw band value 5 is flat; the UI shows -5..+5 steps, as the reference tool does.</summary>
    public const int BandFlat = 5, BandMin = -5, BandMax = 5;

    public static IReadOnlyList<int> BandFrequencies { get; } = [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public static IReadOnlyList<int> AutoOffMinutes { get; } = [0, 5, 15, 30, 45, 60];

    private static readonly (byte Raw, string Id, string ResourceKey, string Name)[] Presets =
    [
        (0x00, "default", "UsbEq_Default", "Default"),
        (0x07, "game", "UsbEq_Game", "Game"),
        (0x08, "music", "UsbEq_Music", "Music"),
        (0x09, "movie", "UsbEq_Movie", "Movie"),
    ];

    /// <summary>Built-in presets. "custom" is listed only while the custom bands themselves could not be read.</summary>
    public static IReadOnlyList<EqualizerPreset> EqualizerPresets(bool customBandsKnown) =>
    [
        .. Presets.Select(p => new EqualizerPreset(p.Id, p.Name, p.ResourceKey, CapabilitySupport.Supported)),
        .. customBandsKnown ? Array.Empty<EqualizerPreset>() : [new EqualizerPreset("custom", "Custom", "UsbEq_Custom", CapabilitySupport.Supported)],
    ];

    public static string? PresetId(byte raw) => raw == CustomPresetId ? "custom" : Presets.FirstOrDefault(p => p.Raw == raw).Id;

    public static byte? PresetValue(string id) => id == "custom" ? (byte)CustomPresetId : Presets.FirstOrDefault(p => p.Id == id) is { Id: not null } p ? p.Raw : null;

    public static int Gain(byte raw) => raw - BandFlat;

    /// <summary>Null when a gain is outside the -5..+5 range.</summary>
    public static byte[]? Bands(IReadOnlyList<int> gains)
    {
        if (gains.Count != BandFrequencies.Count || gains.Any(g => g is < BandMin or > BandMax)) return null;
        return gains.Select(g => (byte)(g + BandFlat)).ToArray();
    }
}
