// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Google;

/// <summary>Built-in Pixel Buds profiles. Only the two Maestro models HeyBuds lists; no family fallback.</summary>
public static class GoogleProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        new GoogleProfile("google-pixel-buds-pro", "Pixel Buds Pro"),
        new GoogleProfile("google-pixel-buds-pro-2", "Pixel Buds Pro 2"),
    ];

    internal static string Normalize(string? value) => new((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// One Pixel Buds model. Noise control (off / ANC / transparency) and the five EQ presets are
/// offered only after the earbuds returned a valid setting read-back on this connection.
/// </summary>
public sealed class GoogleProfile : IControllableProfile
{
    private const string Evidence = "Maestro protocol as ported by HeyBuds (PixelBudsMacOS / pbpctrl facts); not hardware verified.";

    internal GoogleProfile(string modelId, string displayName, bool noiseConfirmed = false, bool equalizerConfirmed = false)
    {
        ModelId = modelId;
        DisplayName = displayName;
        NoiseConfirmed = noiseConfirmed;
        EqualizerConfirmed = equalizerConfirmed;
        Capabilities = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, Evidence)
            .Set(DeviceCapability.NoiseCancellation, noiseConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Not opened by the reference implementation.")
            .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Unsupported, "Not implemented.")
            .Set(DeviceCapability.Equalizer, equalizerConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Arbitrary curves are not offered.")
            .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, Evidence)
            .Build();
    }

    public string Brand => "google";

    public bool IsGeneric => false;

    public string ModelId { get; }

    public string DisplayName { get; }

    public string ImageKey => ModelId;

    public DeviceForm Form => DeviceForm.Earbuds;

    public IDeviceCapabilities Capabilities { get; }

    internal bool NoiseConfirmed { get; }

    internal bool EqualizerConfirmed { get; }

    internal GoogleProfile With(bool? noise = null, bool? equalizer = null) =>
        new(ModelId, DisplayName, noise ?? NoiseConfirmed, equalizer ?? EqualizerConfirmed);

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => NoiseConfirmed ? Options : [];

    private static readonly IReadOnlyList<NoiseControlOption> Options =
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
    ];

    internal static readonly (string Name, string Key)[] PresetNames =
    [
        ("Balanced", "Google_Eq_Balanced"), ("Heavy bass", "Google_Eq_HeavyBass"), ("Vocal boost", "Google_Eq_VocalBoost"),
        ("Treble boost", "Google_Eq_TrebleBoost"), ("Light bass", "Google_Eq_LightBass"),
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => EqualizerConfirmed
        ? [.. PresetNames.Select((p, i) => new EqualizerPreset(i.ToString(), p.Name, p.Key, CapabilitySupport.Supported))]
        : [];

    public IReadOnlyList<Guid> ControlServiceUuids => [GoogleMaestro.ServiceUuid];

    /// <summary>Exact model name, optionally prefixed "Google"; "Pixel Buds Pro 2 Case" or later models never match.</summary>
    public int MatchScore(DeviceIdentity identity)
    {
        string name = GoogleProfiles.Normalize(identity.Name), expected = GoogleProfiles.Normalize(DisplayName);
        return name.Length > 0 && (name == expected || name == "google" + expected) ? 100 : 0;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new GoogleDeviceController(channel, context, this);

    public override string ToString() => ModelId;
}
