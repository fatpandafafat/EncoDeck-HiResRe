// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Revision-specific status offsets from GalaxyBudsClient (GPL-3.0) and Budsy (MIT), as attributed by HeyBuds.
using System.Text.RegularExpressions;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Samsung;

public enum SamsungModel
{
    Buds2,
    Buds2Pro,
    Buds4Pro,
}

/// <summary>Fields of one extended status message (0x61) that EncoDeck uses.</summary>
/// <param name="Charging">Left, right and case charging flags; null when this revision has no confirmed field.</param>
/// <param name="Equalizer">Preset 0–5, or null for a value outside the preset range.</param>
/// <param name="AncLevel">Buds4 Pro ANC level 0–4, or null when the field is absent or out of range.</param>
public sealed record SamsungStatus(byte Revision, byte Left, byte Right, byte Case, (bool Left, bool Right, bool Case)? Charging,
    NoiseControlMode Noise, byte? Equalizer, byte TouchFlags, byte? AncLevel)
{
    /// <summary>Bit 7 set means touch controls are enabled.</summary>
    public bool TouchLocked => (TouchFlags & 0x80) == 0;
}

/// <summary>Galaxy Buds2, Buds2 Pro and Buds4 Pro over the modern SPP protocol.</summary>
public static partial class SamsungProfiles
{
    public static readonly Guid ServiceId = new("2E73A4AD-332D-41FC-90E2-16BEF06523F2");

    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. Enum.GetValues<SamsungModel>().Select(m => new SamsungProfile(m))];

    public static string DisplayName(SamsungModel model) => model switch
    {
        SamsungModel.Buds2 => "Galaxy Buds2",
        SamsungModel.Buds2Pro => "Galaxy Buds2 Pro",
        _ => "Galaxy Buds4 Pro",
    };

    /// <summary>
    /// Exact model names only. Samsung appends a four-digit hexadecimal pairing suffix; other
    /// suffixes (FE, a newer generation, "Pro Max") must not fall back to a family prefix.
    /// </summary>
    public static SamsungModel? Named(string? name)
    {
        string trimmed = PairingSuffix().Replace((name ?? string.Empty).Trim(), string.Empty);
        string n = Normalize(trimmed);
        foreach (var model in Enum.GetValues<SamsungModel>())
        {
            string expected = Normalize(DisplayName(model));
            if (n == expected || n == "samsung" + expected) return model;
        }
        return null;
    }

    /// <summary>Shortest extended status each revision carries (GalaxyBudsClient decoder branches).</summary>
    public static int MinimumStatusLength(SamsungModel model, byte revision) => model switch
    {
        SamsungModel.Buds2 => revision >= 10 ? 37 : revision >= 8 ? 36 : revision >= 7 ? 35 : revision >= 6 ? 34
            : revision >= 5 ? 33 : revision >= 3 ? 29 : revision >= 2 ? 28 : 27,
        SamsungModel.Buds2Pro => revision >= 13 ? 45 : revision >= 11 ? 44 : revision >= 8 ? 43 : revision >= 3 ? 42
            : revision >= 1 ? 41 : 34,
        // Verified common prefix and ANC level only; no newer offsets are assumed.
        _ => 25,
    };

    /// <summary>Parses an extended status, or null when it is truncated or carries an unknown noise mode.</summary>
    public static SamsungStatus? ParseStatus(SamsungModel model, ReadOnlySpan<byte> p)
    {
        if (p.Length == 0) return null;
        byte revision = p[0];
        if (p.Length < MinimumStatusLength(model, revision) || p[12] > (model == SamsungModel.Buds4Pro ? 3 : 2)) return null;
        byte? charge = model switch
        {
            SamsungModel.Buds2 when revision >= 10 => p[36],
            SamsungModel.Buds2Pro when revision >= 11 => p[43],
            // The new-generation charge layout needs separate evidence.
            _ => null,
        };
        var noise = p[12] switch
        {
            0 => NoiseControlMode.Off,
            1 => NoiseControlMode.NoiseCancellation,
            2 => NoiseControlMode.Transparency,
            _ => NoiseControlMode.Adaptive,
        };
        return new SamsungStatus(revision, p[2], p[3], p[7],
            charge is { } c ? ((c & 0x10) != 0, (c & 0x04) != 0, (c & 0x01) != 0) : null,
            noise, p[9] <= 5 ? p[9] : null, p[10],
            model == SamsungModel.Buds4Pro && p[24] <= 4 ? p[24] : null);
    }

    /// <summary>
    /// Touch-lock payload: [!lockAll, tap, double tap, triple tap, hold, (call double tap, call hold)].
    /// Keeps every individual gesture preference as the device last reported it. The call gestures
    /// exist on Buds2 revision 7+ and Buds2 Pro revision 1+. Buds4 touch settings are not ported.
    /// </summary>
    public static byte[]? TouchLockPayload(SamsungModel model, bool locked, SamsungStatus current)
    {
        if (model == SamsungModel.Buds4Pro) return null;
        int[] bits = (model == SamsungModel.Buds2 && current.Revision >= 7) || (model == SamsungModel.Buds2Pro && current.Revision >= 1)
            ? [3, 2, 1, 0, 4, 5] : [3, 2, 1, 0];
        return [locked ? (byte)0 : (byte)1, .. bits.Select(b => (byte)((current.TouchFlags >> b) & 1))];
    }

    public static byte? NoiseByte(SamsungModel model, NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.Off => 0,
        NoiseControlMode.NoiseCancellation => 1,
        NoiseControlMode.Transparency => 2,
        NoiseControlMode.Adaptive when model == SamsungModel.Buds4Pro => 3,
        _ => null,
    };

    internal static string Normalize(string? value) =>
        new((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex(@"\s*\([0-9a-fA-F]{4}\)$")]
    private static partial Regex PairingSuffix();
}

/// <summary>
/// One Galaxy Buds model. Noise control, presets, the Buds4 Pro ANC level and the Buds2 / Buds2 Pro
/// touch lock are offered only after a valid extended status has been read from the earbuds.
/// </summary>
public sealed class SamsungProfile : IControllableProfile
{
    private const string Evidence = "Galaxy Buds SPP protocol as implemented by HeyBuds (Budsy / GalaxyBudsClient facts); simulated-device tests only, no hardware verification.";

    internal static readonly (string Id, string Name, string Key)[] Presets =
    [
        ("0", "Normal", "Samsung_Eq_Normal"), ("1", "Bass boost", "Samsung_Eq_BassBoost"),
        ("2", "Soft", "Samsung_Eq_Soft"), ("3", "Dynamic", "Samsung_Eq_Dynamic"),
        ("4", "Clear", "Samsung_Eq_Clear"), ("5", "Treble boost", "Samsung_Eq_TrebleBoost"),
    ];

    internal SamsungProfile(SamsungModel model, bool statusConfirmed = false, bool equalizerConfirmed = false, bool ancLevelConfirmed = false)
    {
        Model = model;
        StatusConfirmed = statusConfirmed;
        EqualizerConfirmed = statusConfirmed && equalizerConfirmed;
        AncLevelConfirmed = statusConfirmed && ancLevelConfirmed && model == SamsungModel.Buds4Pro;
        CapabilitySupport Confirmed(bool value) => value ? CapabilitySupport.Supported : CapabilitySupport.Unknown;
        Capabilities = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
            .Set(DeviceCapability.ChargingStatus, model == SamsungModel.Buds4Pro ? CapabilitySupport.Unsupported : CapabilitySupport.Supported,
                model == SamsungModel.Buds4Pro ? "No confirmed charge field for this generation." : "Charge flags on Buds2 revision 10+ / Buds2 Pro revision 11+ only.")
            .Set(DeviceCapability.NoiseCancellation, Confirmed(StatusConfirmed), Evidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, model == SamsungModel.Buds4Pro ? Confirmed(StatusConfirmed) : CapabilitySupport.Unsupported, Evidence)
            .Set(DeviceCapability.NoiseCancellationLevel, model == SamsungModel.Buds4Pro ? Confirmed(AncLevelConfirmed) : CapabilitySupport.Unsupported, Evidence)
            .Set(DeviceCapability.Equalizer, Confirmed(EqualizerConfirmed), Evidence)
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Not implemented.")
            .Build();
    }

    public SamsungModel Model { get; }

    internal bool StatusConfirmed { get; }

    internal bool EqualizerConfirmed { get; }

    internal bool AncLevelConfirmed { get; }

    /// <summary>The profile after a status read; the same instance when nothing changes.</summary>
    internal SamsungProfile WithStatus(SamsungStatus? status)
    {
        bool confirmed = status is not null, eq = status?.Equalizer is not null, level = status?.AncLevel is not null;
        return confirmed == StatusConfirmed && eq == EqualizerConfirmed && (level && Model == SamsungModel.Buds4Pro) == AncLevelConfirmed
            ? this : new SamsungProfile(Model, confirmed, eq, level);
    }

    public string Brand => "samsung";

    public bool IsGeneric => false;

    public string ModelId => Model switch
    {
        SamsungModel.Buds2 => "samsung-buds2",
        SamsungModel.Buds2Pro => "samsung-buds2-pro",
        _ => "samsung-buds4-pro",
    };

    public string DisplayName => SamsungProfiles.DisplayName(Model);

    public string ImageKey => ModelId;

    public DeviceForm Form => DeviceForm.Earbuds;

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyCollection<DeviceToggle> Toggles => StatusConfirmed && Model != SamsungModel.Buds4Pro ? [DeviceToggle.TouchControls] : [];

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => !StatusConfirmed ? [] :
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        .. AncLevelConfirmed
            ? Enumerable.Range(1, 5).Select(i => new NoiseControlOption(NoiseControlMode.NoiseCancellation, $"level{i}", $"Level {i}", $"Samsung_AncLevel_{i}", CapabilitySupport.Supported))
            : [],
        new(NoiseControlMode.Transparency, null, "Ambient sound", "NoiseMode_Transparency", CapabilitySupport.Supported),
        .. Model == SamsungModel.Buds4Pro ? new[] { new NoiseControlOption(NoiseControlMode.Adaptive, null, "Adaptive", "NoiseMode_Adaptive", CapabilitySupport.Supported) } : [],
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => !EqualizerConfirmed ? [] :
        [.. Presets.Select(p => new EqualizerPreset(p.Id, p.Name, p.Key, CapabilitySupport.Supported))];

    public IReadOnlyList<Guid> ControlServiceUuids => [SamsungProfiles.ServiceId];

    public int MatchScore(DeviceIdentity identity) => SamsungProfiles.Named(identity.Name) == Model ? 100 : 0;

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new SamsungDeviceController(channel, context, new SamsungProfile(Model));

    public override string ToString() => ModelId;
}
