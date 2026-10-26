// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Nothing;

/// <summary>Built-in Nothing / CMF profiles: four exact models, no name-only family fallback.</summary>
public static class NothingProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() => [.. NothingModel.All.Select(m => new NothingProfile(m))];
}

/// <summary>
/// One Nothing / CMF model. Noise control and EQ presets are offered only after this connection
/// read them back successfully; until then the profile offers battery only.
/// </summary>
public sealed class NothingProfile : IControllableProfile
{
    private const string Evidence = "HeyBuds Nothing / CMF port (swift-nothing-ear, nothing-x-macos protocol facts); not hardware verified.";

    internal NothingProfile(NothingModel model, bool noiseConfirmed = false, bool eqConfirmed = false)
    {
        Model = model;
        NoiseConfirmed = noiseConfirmed;
        EqConfirmed = eqConfirmed;
        Capabilities = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Evidence)
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, Evidence)
            .Set(DeviceCapability.NoiseCancellation, noiseConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
            .Set(DeviceCapability.Equalizer, eqConfirmed ? CapabilitySupport.Supported : CapabilitySupport.Unknown, Evidence)
            .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, Evidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Not offered by the HeyBuds profile.")
            .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Unsupported, "Not offered by the HeyBuds profile.")
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Advanced / custom EQ is not implemented.")
            .Build();
    }

    internal NothingModel Model { get; }

    internal bool NoiseConfirmed { get; }

    internal bool EqConfirmed { get; }

    internal NothingProfile WithConfirmed(bool noise, bool eq) =>
        noise == NoiseConfirmed && eq == EqConfirmed ? this : new(Model, noise, eq);

    public string Brand => "nothing";

    public bool IsGeneric => false;

    public string ModelId => Model.ModelId;

    public string DisplayName => Model.DisplayName;

    public string ImageKey => ModelId;

    public DeviceForm Form => Model.SingleBattery ? DeviceForm.Headphones : DeviceForm.Earbuds;

    public IDeviceCapabilities Capabilities { get; }

    /// <summary>HeyBuds offers off / ANC / transparency for all four models; levels and adaptive are read-only.</summary>
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => NoiseConfirmed ? Options : [];

    private static readonly IReadOnlyList<NoiseControlOption> Options =
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
    ];

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => !EqConfirmed ? [] :
        [.. Model.PresetIds.Select(id => Model.PresetLabel(id)!)
            .Select((label, id) => new EqualizerPreset(id.ToString(), label, "Nothing_Eq_" + label.Replace(" ", "", StringComparison.Ordinal), CapabilitySupport.Supported))];

    public IReadOnlyList<Guid> ControlServiceUuids => [NothingModel.ServiceId];

    /// <summary>Exact alias only: "CMF Buds 2a" or "Nothing Ear (a) Pro" are other products.</summary>
    public int MatchScore(DeviceIdentity identity) => NothingModel.Named(identity.Name) == Model ? 100 : 0;

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new NothingDeviceController(channel, context, this);

    public override string ToString() => ModelId;
}
