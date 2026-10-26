// SPDX-License-Identifier: GPL-3.0-only
// Gesture, wear-detection switch and dual connection evidence from TWS-Pods-PC
// (https://github.com/Zhaoyi-ya/TWS-Pods-PC, 3350f42) and vivopods-mac
// (https://github.com/FxRayHughes/vivopods-mac, be25133), both GPL-3.0-only.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>Settings the earbuds have answered for in this session; each is offered only after a valid read-back.</summary>
[Flags]
public enum VivoConfirmed
{
    None = 0,
    DoubleTap = 1,
    LongPress = 2,
    NoiseCycle = 4,
    WearDetectionSwitch = 8,
    SourceDevices = 16,
}

/// <summary><see cref="IControllableProfile"/> for one vivo / iQOO model (or the generic fallback).</summary>
public sealed class VivoDeviceProfile : IControllableProfile, IBrandGestureProfile
{
    private const string CatalogEvidence = "Declared by the official vivo model configuration.";
    private const string ProtocolEvidence = "Implemented for every vivo GAIA device.";
    private const string Tws3eEvidence = "Community btsnoop replay on vivo TWS 3e fw 2.5.9 (TWS-Pods-PC); offered after the earbuds report the setting.";
    private const string Tws5Evidence = "Community real-device test on vivo TWS 5 fw 2.11.1 (vivopods-mac); offered after the earbuds report the list.";
    private const string ProtocolOnlyEvidence = "Same command as the community-tested model; not verified on this model, so experimental after read-back.";

    private readonly string _normalizedName;
    private readonly string? _normalizedProject;
    private readonly bool _offerExperimental;

    public VivoDeviceProfile(VivoModel model, bool isGeneric = false, VivoConfirmed confirmed = VivoConfirmed.None, bool offerExperimental = false)
    {
        Model = model;
        IsGeneric = isGeneric;
        Confirmed = confirmed;
        _offerExperimental = offerExperimental;
        _normalizedName = VivoModel.Normalize(model.Name);
        _normalizedProject = model.ProjectName is { } p ? VivoModel.Normalize(p) : null;
        Capabilities = BuildCapabilities(model, isGeneric, confirmed);
        GestureSlots = model.Headphones || isGeneric ? [] : VivoGestures.Slots;
        NoiseControlOptions = model.Noise
            ?
            [
                new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
                new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
                new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
            ]
            : [];
        EqualizerPresets = model.AudioEffectIds
            .Select(id =>
            {
                var (english, key) = VivoCatalog.EffectName(id);
                return new EqualizerPreset(VivoCatalog.PresetId(id), english, key, CapabilitySupport.Supported);
            })
            .ToArray();
    }

    public VivoModel Model { get; }

    /// <summary>Settings confirmed by a read-back in the current session.</summary>
    public VivoConfirmed Confirmed { get; }

    public string Brand => "vivo";

    public bool IsGeneric { get; }

    public string ModelId => Model.ModelId;

    public string DisplayName => Model.DisplayName;

    public string ImageKey => Model.ModelId;

    public DeviceForm Form => Model.Headphones ? DeviceForm.Headphones : DeviceForm.Earbuds;

    /// <summary>
    /// Game mode as declared; the wear-detection switch once read back, and on models other than the
    /// community-tested TWS 3e only while experimental features are enabled.
    /// </summary>
    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. Capabilities.Get(DeviceCapability.GamingMode).Support == CapabilitySupport.Supported ? new[] { DeviceToggle.GamingMode } : [],
        .. OffersWearDetectionSwitch ? new[] { DeviceToggle.InEarDetection } : [],
    ];

    internal bool OffersWearDetectionSwitch =>
        Confirmed.HasFlag(VivoConfirmed.WearDetectionSwitch) && (IsTws3e(Model) || _offerExperimental);

    public IReadOnlyList<BrandGestureSlot> GestureSlots { get; }

    /// <summary>Same model with the settings confirmed so far.</summary>
    internal VivoDeviceProfile WithConfirmed(VivoConfirmed confirmed, bool offerExperimental) =>
        confirmed == Confirmed && offerExperimental == _offerExperimental ? this : new(Model, IsGeneric, confirmed, offerExperimental);

    /// <summary>Whether the dual connection list may be queried: declared, and tested on this model or opted in.</summary>
    internal bool MayQuerySourceDevices(bool allowExperimental) =>
        Model.DualConnection && !IsGeneric && (IsTws5(Model) || allowExperimental);

    private static bool IsTws3e(VivoModel model) => model.ModelId == "vivo-tws-3e";

    private static bool IsTws5(VivoModel model) => model.ModelId == "vivo-tws-5";

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }

    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }

    /// <summary>The RFCOMM service first; the GAIA GATT service (no frame header) when RFCOMM is unavailable.</summary>
    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [VivoConstants.ServiceUuid, VivoConstants.GattServiceUuid];

    /// <summary>
    /// 100 for the exact official name, 80 when the name carries the model's unique project code
    /// (e.g. <c>DPD2321A LE</c>), 40–70 when it contains the official name (longer names win), and
    /// 5 for the generic profile on any <c>vivo TWS</c> / <c>iQOO TWS</c> / vivo-iQOO headphones name.
    /// </summary>
    public int MatchScore(DeviceIdentity identity)
    {
        string name = VivoModel.Normalize(identity.Name);
        if (name.Length == 0) return 0;
        if (IsGeneric) return IsFamilyName(name) ? 5 : 0;
        if (name == _normalizedName) return 100;
        if (_normalizedProject is not null && Tokens(identity.Name).Contains(_normalizedProject)) return 80;
        if (name.Contains(_normalizedName, StringComparison.Ordinal)) return 40 + Math.Min(_normalizedName.Length, 30);
        return 0;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new VivoDeviceController(channel, context, this);

    internal static bool IsFamilyName(string normalized) =>
        normalized.StartsWith("vivotws", StringComparison.Ordinal)
        || normalized.StartsWith("iqootws", StringComparison.Ordinal)
        || normalized.StartsWith("vivoheadphones", StringComparison.Ordinal)
        || normalized.StartsWith("iqooheadphones", StringComparison.Ordinal);

    private static HashSet<string> Tokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = VivoModel.Normalize(part);
            if (normalized.Length > 0) tokens.Add(normalized);
        }
        return tokens;
    }

    private static DeviceCapabilities BuildCapabilities(VivoModel model, bool isGeneric, VivoConfirmed confirmed)
    {
        var b = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, ProtocolEvidence)
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, ProtocolEvidence)
            .Set(DeviceCapability.FirmwareInformation, CapabilitySupport.Supported, ProtocolEvidence)
            .Set(DeviceCapability.AdaptiveNoiseCancellation, CapabilitySupport.Unsupported, "No adaptive mode in the vivo protocol.")
            .Set(DeviceCapability.NoiseCancellationLevel, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            // 0x0139 had no audible effect on a community TWS Air3 Pro test, so it stays off.
            .Set(DeviceCapability.SpatialAudio, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.HeadTracking, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.FitTest, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.PromptVolume, CapabilitySupport.Unsupported, "Not implemented for vivo.")
            .Set(DeviceCapability.BassEngine, CapabilitySupport.Unsupported, "Not implemented for vivo.");

        // Live wear state is only meaningful on earbuds.
        b.Set(DeviceCapability.InEarDetection, model.Headphones || isGeneric ? CapabilitySupport.Unknown : CapabilitySupport.Supported, ProtocolEvidence);
        b.Set(DeviceCapability.NoiseCancellation, Declared(model.Noise), CatalogEvidence);
        b.Set(DeviceCapability.Equalizer, Declared(model.AudioEffect > 0), CatalogEvidence);
        b.Set(DeviceCapability.FindMyEarbuds, Declared(model.Find), CatalogEvidence);
        b.Set(DeviceCapability.GamingMode, Declared(model.LowLatency), CatalogEvidence);

        const VivoConfirmed gestures = VivoConfirmed.DoubleTap | VivoConfirmed.LongPress | VivoConfirmed.NoiseCycle;
        if (model.Headphones || isGeneric)
        {
            b.Set(DeviceCapability.TouchCustomization, CapabilitySupport.Unknown, "No gesture evidence for this model.");
        }
        else if ((confirmed & gestures) != 0)
        {
            b.Set(DeviceCapability.TouchCustomization, IsTws3e(model) ? CapabilitySupport.Supported : CapabilitySupport.Experimental,
                IsTws3e(model) ? Tws3eEvidence : ProtocolOnlyEvidence);
        }
        else
        {
            b.Set(DeviceCapability.TouchCustomization, CapabilitySupport.Unknown, "Offered after the earbuds report their gesture settings.");
        }

        if (!model.DualConnection || isGeneric)
        {
            b.Set(DeviceCapability.DualDeviceConnection, isGeneric ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported,
                "No manageable dual connection list declared.");
        }
        else if (confirmed.HasFlag(VivoConfirmed.SourceDevices))
        {
            b.Set(DeviceCapability.DualDeviceConnection, IsTws5(model) ? CapabilitySupport.Supported : CapabilitySupport.Experimental,
                IsTws5(model) ? Tws5Evidence : ProtocolOnlyEvidence);
        }
        else
        {
            b.Set(DeviceCapability.DualDeviceConnection, CapabilitySupport.Unknown, "Offered after the earbuds report the device list.");
        }
        return b.Build();

        CapabilitySupport Declared(bool yes) => yes
            ? CapabilitySupport.Supported
            : isGeneric ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported;
    }
}

/// <summary>Built-in vivo / iQOO profiles.</summary>
public static class VivoProfiles
{
    /// <summary>One profile per catalog model plus the generic fallback.</summary>
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
        [.. VivoCatalog.All.Select(m => new VivoDeviceProfile(m)), new VivoDeviceProfile(VivoCatalog.Generic, isGeneric: true)];
}
