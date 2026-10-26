// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Model routing and mode layouts from bosectl and bozo (MIT) as attributed by HeyBuds.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Bose;

/// <summary>Headphones with a dedicated BMAP RFCOMM profile. Ultra Earbuds (2nd Gen) needs secure BLE and is not ported.</summary>
public enum BoseModel
{
    QuietComfort45,
    QuietComfortUltraHeadphones2,
}

public static class BoseModelExtensions
{
    public static int MaximumModeCount(this BoseModel model) => model == BoseModel.QuietComfort45 ? 4 : 11;

    public static int ModeStatusLength(this BoseModel model) => model == BoseModel.QuietComfort45 ? 47 : 48;
}

public static class BoseProfiles
{
    /// <summary>BMAP SDP service; the RFCOMM channel is always resolved from it, never fixed.</summary>
    public static readonly Guid BmapServiceUuid = new("00000000-DECA-FADE-DECA-DEAFDECACAFF");

    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        new BoseProfile(BoseModel.QuietComfort45),
        new BoseProfile(BoseModel.QuietComfortUltraHeadphones2),
    ];

    /// <summary>Exact model names only; other generations and look-alike names are not claimed.</summary>
    public static BoseModel? Named(string? name)
    {
        string n = new((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        string bare = n.StartsWith("bose", StringComparison.Ordinal) ? n[4..] : n;
        return bare switch
        {
            "qc45" or "quietcomfort45" => BoseModel.QuietComfort45,
            "qcultraheadphones2ndgen" or "qcultraheadphones2ndgeneration" or "qcultraheadphones2" or "quietcomfortultraheadphones2"
                or "quietcomfortultraheadphones2ndgen" or "quietcomfortultraheadphones2ndgeneration" => BoseModel.QuietComfortUltraHeadphones2,
            _ => null,
        };
    }
}

/// <summary>
/// Bose BMAP headphones: battery, firmware and the fixed Quiet / Aware modes. Noise control is
/// offered only after the device's mode list shows both presets.
/// </summary>
public sealed class BoseProfile : IControllableProfile
{
    private const string Evidence = "BMAP protocol facts via HeyBuds (bosectl / bozo); simulated device tests only, not hardware verified.";

    internal BoseProfile(BoseModel model, bool noiseConfirmed = false)
    {
        Model = model;
        NoiseConfirmed = noiseConfirmed;
    }

    public BoseModel Model { get; }

    /// <summary>The live mode list showed non-editable Quiet at index 0 and Aware at index 1.</summary>
    internal bool NoiseConfirmed { get; }

    internal BoseProfile WithNoiseConfirmed(bool confirmed) => confirmed == NoiseConfirmed ? this : new(Model, confirmed);

    public string Brand => "bose";

    public bool IsGeneric => false;

    public string ModelId => Model == BoseModel.QuietComfort45 ? "bose-qc45" : "bose-qc-ultra-headphones-2";

    public string DisplayName => Model == BoseModel.QuietComfort45 ? "Bose QuietComfort 45" : "Bose QuietComfort Ultra Headphones (2nd Gen)";

    public string ImageKey => ModelId;

    public DeviceForm Form => DeviceForm.Headphones;

    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
        .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Unsupported, "Charging is not reported by the battery component.")
        .Set(DeviceCapability.NoiseCancellation, NoiseConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
        .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Not implemented.")
        .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Unsupported, "CNC level is not implemented.")
        .Set(DeviceCapability.Equalizer, CapabilitySupport.Unsupported, "Not implemented.")
        .Build();

    // BMAP modes have no "off"; Quiet and Aware are the two fixed presets.
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => NoiseConfirmed ? Options : [];

    private static readonly IReadOnlyList<NoiseControlOption> Options =
    [
        new(NoiseControlMode.NoiseCancellation, null, "Quiet", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Aware", "NoiseMode_Transparency", CapabilitySupport.Supported),
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];

    public IReadOnlyList<Guid> ControlServiceUuids => [BoseProfiles.BmapServiceUuid];

    public int MatchScore(DeviceIdentity identity) => BoseProfiles.Named(identity.Name) == Model ? 100 : 0;

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new BoseDeviceController(channel, context, this);

    public override string ToString() => ModelId;
}
