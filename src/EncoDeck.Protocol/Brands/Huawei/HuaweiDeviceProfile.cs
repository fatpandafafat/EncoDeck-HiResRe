// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>
/// <see cref="IControllableProfile"/> for one HUAWEI model (or the generic HUAWEI fallback). Feature
/// switches and the EQ preset list are narrowed by what the earbuds report during the session.
/// </summary>
public sealed class HuaweiDeviceProfile : IControllableProfile
{
    private const string Evidence = "OppoPodsManager/OpenFreebuds/HuaweiPods per-model tables; not verified on hardware by EncoDeck.";

    private readonly IReadOnlyCollection<DeviceToggle> _reportedToggles;
    private readonly IReadOnlyList<byte>? _reportedEq;

    public HuaweiDeviceProfile(HuaweiModel model, bool spatialConfirmed = false)
        : this(model, spatialConfirmed, [], null)
    {
    }

    private HuaweiDeviceProfile(HuaweiModel model, bool spatialConfirmed, IReadOnlyCollection<DeviceToggle> reportedToggles, IReadOnlyList<byte>? reportedEq)
    {
        Model = model;
        _reportedToggles = reportedToggles;
        _reportedEq = reportedEq;
        NoiseControlOptions = BuildNoiseOptions(model);
        // The earbuds list the built-in presets they offer (EQ TLV 3); presets they do not list stay hidden.
        EqualizerPresets = [.. model.EqPresets
            .Where(p => reportedEq is null || reportedEq.Contains(p.ProtocolId))
            .Select(p => new EqualizerPreset(p.PresetId, p.DisplayName, p.ResourceKey, CapabilitySupport.Supported))];
        SpatialConfirmed = spatialConfirmed;
        Capabilities = BuildCapabilities(model, spatialConfirmed, EqualizerPresets.Count > 0);
    }

    public HuaweiModel Model { get; }
    public bool SpatialConfirmed { get; }
    internal HuaweiDeviceProfile WithSpatial() => SpatialConfirmed ? this : new(Model, true, _reportedToggles, _reportedEq);

    /// <summary>Opens a feature switch the earbuds just reported a valid state for.</summary>
    internal HuaweiDeviceProfile WithReportedToggle(DeviceToggle toggle) =>
        _reportedToggles.Contains(toggle) ? this : new(Model, SpatialConfirmed, [.. _reportedToggles, toggle], _reportedEq);

    /// <summary>Limits the EQ presets to the built-in ids the earbuds reported.</summary>
    internal HuaweiDeviceProfile WithReportedEqualizer(IReadOnlyList<byte> ids) =>
        _reportedEq is not null && _reportedEq.SequenceEqual(ids) ? this : new(Model, SpatialConfirmed, _reportedToggles, [.. ids]);

    public string Brand => "huawei";

    public bool IsGeneric => Model.IsGeneric;

    public string ModelId => Model.ModelId;

    public string DisplayName => Model.DisplayName;

    public string ImageKey => Model.ModelId;

    public DeviceForm Form => Model.HasChargingCase ? DeviceForm.Earbuds : DeviceForm.Headphones;

    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. Model.WearDetection ? [DeviceToggle.InEarDetection] : Array.Empty<DeviceToggle>(),
        .. Model.LowLatency ? [DeviceToggle.GamingMode] : Array.Empty<DeviceToggle>(),
        .. Model.DualConnect ? [DeviceToggle.DualDeviceConnection] : Array.Empty<DeviceToggle>(),
        .. _reportedToggles,
    ];

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [HuaweiCommands.SppServiceUuid];

    public int MatchScore(DeviceIdentity identity)
    {
        if (Model.IsGeneric) return HuaweiModels.IsFamilyName(identity.Name) ? 1 : 0;
        var detected = HuaweiModels.Detect(identity.Name, out bool exact);
        if (!ReferenceEquals(detected, Model)) return 0;
        return exact ? 100 : 50;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new HuaweiDeviceController(channel, context, this, HuaweiWire.CreateCodec(Model.Protocol));

    private static NoiseControlOption[] BuildNoiseOptions(HuaweiModel model)
    {
        if (!model.NoiseControlUsable) return [];
        var options = new List<NoiseControlOption>
        {
            new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        };
        if (model.AncLevels.Count > 0)
        {
            options.AddRange(model.AncLevels.Select(l => new NoiseControlOption(NoiseControlMode.NoiseCancellation, l.LevelId, l.DisplayName, l.ResourceKey, CapabilitySupport.Supported)));
        }
        else
        {
            options.Add(new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported));
        }
        if (model.Transparency && model.TransparencyLevels.Count > 0)
        {
            options.AddRange(model.TransparencyLevels.Select(l => new NoiseControlOption(NoiseControlMode.Transparency, l.LevelId, l.DisplayName, l.ResourceKey, CapabilitySupport.Supported)));
        }
        else if (model.Transparency)
        {
            options.Add(new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported));
        }
        return [.. options];
    }

    private static DeviceCapabilities BuildCapabilities(HuaweiModel model, bool spatialConfirmed, bool hasEq)
    {
        static CapabilitySupport S(bool on) => on ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;
        return DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, S(model.RfcommBattery), Evidence)
            .Set(DeviceCapability.ChargingStatus, S(model.RfcommBattery), Evidence)
            .Set(DeviceCapability.NoiseCancellation, S(model.NoiseControlUsable), Evidence)
            .Set(DeviceCapability.NoiseCancellationLevel, S(model.NoiseControlUsable && model.AncLevels.Count > 1), Evidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "Dynamic ANC is exposed as a noise-cancelling level.")
            .Set(DeviceCapability.Equalizer, S(hasEq), Evidence)
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "HUAWEI custom EQ is not ported; a selected custom preset is shown as its raw id.")
            .Set(DeviceCapability.InEarDetection, S(model.WearDetection), Evidence)
            .Set(DeviceCapability.GamingMode, S(model.LowLatency), Evidence)
            .Set(DeviceCapability.DualDeviceConnection, S(model.DualConnect), Evidence)
            .Set(DeviceCapability.SpatialAudio, S(spatialConfirmed), "MBB feature 24, enabled after live readback.")
            .Set(DeviceCapability.HeadTracking, S(spatialConfirmed), "Independent spatial audio mode 1, confirmed by device.")
            .Set(DeviceCapability.FindMyEarbuds, CapabilitySupport.Unsupported, "Find-device commands are unverified upstream.")
            .Build();
    }
}

/// <summary>Built-in HUAWEI profiles.</summary>
public static class HuaweiProfiles
{
    /// <summary>Every model in <see cref="HuaweiModels.All"/> plus the generic fallback.</summary>
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. HuaweiModels.All.Select(m => new HuaweiDeviceProfile(m)), new HuaweiDeviceProfile(HuaweiModels.Generic)];
}
