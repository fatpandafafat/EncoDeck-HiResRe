// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Feature evidence from BudsLink (https://github.com/maniacx/BudsLink, GPL-3.0), redmi-buds-bar by
// robin-liquidium (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0) and mebuds
// (https://github.com/paradox-ng/mebuds, GPL-3.0-or-later); see THIRD_PARTY_NOTICES.md.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Family-level profile for Xiaomi / Redmi earbuds. Earbuds are recognized by name; the model
/// (catalog option lists, community notes) is looked up by name or by the VID/PID the earbuds
/// report, and every feature is declared only once the earbuds answered for it.
/// </summary>
public sealed class XiaomiDeviceProfile : IControllableProfile, IBrandGestureProfile
{
    private const string ProtocolNote = "Community protocol notes (BudsLink, redmi-buds-bar, mebuds); enabled after a live readback.";

    private readonly string[] _nameMarkers;
    private readonly int _score;
    private readonly XiaomiModelFeatures? _model;
    private readonly XiaomiCommunityModel? _community;
    private readonly IReadOnlySet<ushort> _confirmed;

    private XiaomiDeviceProfile(string modelId, string displayName, string[] nameMarkers, int score, bool gamingMode,
        XiaomiModelFeatures? model = null, XiaomiCommunityModel? community = null, IReadOnlySet<ushort>? confirmed = null)
    {
        _score = score;
        ModelId = modelId;
        DisplayName = displayName;
        _nameMarkers = nameMarkers;
        HasGamingMode = gamingMode;
        _model = model;
        _community = community;
        _confirmed = confirmed ?? new HashSet<ushort>();
        static CapabilitySupport S(bool on) => on ? CapabilitySupport.Supported : CapabilitySupport.Unknown;
        var caps = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "Device-info record 7: left, right, case (community captures).")
            .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, "Bit 7 of each battery byte (community captures).")
            .Set(DeviceCapability.NoiseCancellation, S(HasNoiseControl), "Config 11 readback on a model with noise control; the firmware also answers on models without ANC.")
            .Set(DeviceCapability.NoiseCancellationLevel, S(HasNoiseControl && (_model?.NoiseLevels.Length ?? 0) > 0), "Levels from the regional product catalogs.")
            .Set(DeviceCapability.AdaptiveNoiseCancellation, S(HasAdaptiveAnc), "Config 37 (adaptive noise cancellation) readback. " + ProtocolNote)
            .Set(DeviceCapability.Equalizer, S(HasEqualizer), "Catalog EQ preset list and config 7 readback.")
            .Set(DeviceCapability.CustomEqualizer, S(HasCustomEqualizer), "Config 55 custom curve readback. " + ProtocolNote)
            .Set(DeviceCapability.DualDeviceConnection, S(HasConfig(XiaomiConfig.DualConnection)), "Config 4, confirmed by live response.")
            .Set(DeviceCapability.SpatialAudio, S(HasConfig(XiaomiConfig.Spatial)), "Config 29 bit 0.")
            .Set(DeviceCapability.HeadTracking, S(HasConfig(XiaomiConfig.Spatial)), "Config 29 bit 3.")
            .Set(DeviceCapability.PromptVolume, S(HasConfig(XiaomiConfig.PromptVolume)), "Config 161 with device-reported bounds.")
            .Set(DeviceCapability.TouchCustomization, S(HasGestures), "Config 2 per-earbud gestures. " + ProtocolNote)
            .Set(DeviceCapability.InEarDetection, S(HasConfig(XiaomiConfig.WearDetectionSwitch)), "Run-info record 10 (inverted). " + ProtocolNote)
            .Set(DeviceCapability.FindMyEarbuds, S(HasFind), "Config 9; refused while config 12 reports an earbud in the ear. " + ProtocolNote);
        caps = gamingMode
            ? caps.Set(DeviceCapability.GamingMode, CapabilitySupport.Supported, "The earbuds answered a read of low-latency config id 47.")
            : caps.Set(DeviceCapability.GamingMode, CapabilitySupport.Unknown, "Low-latency config id 47 has not been read from these earbuds yet.");
        Capabilities = caps.Build();
    }

    /// <summary>Generic Xiaomi earbuds (Xiaomi Buds, Mi True Wireless, FlipBuds, …).</summary>
    public static XiaomiDeviceProfile Xiaomi { get; } = new(
        "xiaomi-earbuds",
        "Xiaomi earbuds",
        ["Xiaomi", "Mi Buds", "Mi True", "Mi Air", "FlipBuds"],
        score: 1,
        gamingMode: false);

    /// <summary>Generic Redmi earbuds (Redmi Buds, Redmi AirDots, …). Wins over <see cref="Xiaomi"/> for names containing both.</summary>
    public static XiaomiDeviceProfile Redmi { get; } = new(
        "redmi-earbuds",
        "Redmi earbuds",
        ["Redmi"],
        score: 2,
        gamingMode: false);

    public string ModelId { get; }

    public string DisplayName { get; }

    public string Brand => "xiaomi";

    public bool IsGeneric => true;

    /// <summary>No product artwork; the app falls back to a generic illustration.</summary>
    public string ImageKey => ModelId;

    /// <summary>Whether the earbuds have answered for low-latency (game) mode.</summary>
    public bool HasGamingMode { get; }

    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. HasGamingMode ? new[] { DeviceToggle.GamingMode } : [],
        .. HasConfig(XiaomiConfig.DualConnection) ? new[] { DeviceToggle.DualDeviceConnection } : [],
        .. HasConfig(XiaomiConfig.WearDetectionSwitch) ? new[] { DeviceToggle.InEarDetection } : [],
        .. HasConfig(XiaomiConfig.AutoAnswer) ? new[] { DeviceToggle.AutoAnswerCalls } : [],
    ];

    /// <summary>
    /// Noise control needs a readback of config 11 and a model known to have ANC: the firmware
    /// answers config 11 on models without it (Redmi Buds 6 Play, REDMI Buds 8 Active).
    /// </summary>
    public bool HasNoiseControl => HasConfig(XiaomiConfig.Noise) && ModelHasAnc;

    public bool HasAdaptiveAnc => HasNoiseControl && HasConfig(XiaomiConfig.AdaptiveAnc);

    public bool HasEqualizer => HasConfig(XiaomiConfig.EqualizerPreset) && (_model?.Equalizers.Length ?? 0) > 0;

    public bool HasCustomEqualizer => HasConfig(XiaomiConfig.EqualizerPreset) && HasConfig(XiaomiConfig.CustomEqualizer);

    public bool HasGestures => HasConfig(XiaomiConfig.Gestures);

    /// <summary>Gesture rows; each appears only after the earbuds reported that gesture.</summary>
    public IReadOnlyList<BrandGestureSlot> GestureSlots => XiaomiGestures.Slots;

    /// <summary>Ringing needs a readable wear state (the in-ear safety check) and a model not known to lack the command.</summary>
    public bool HasFind => HasConfig(XiaomiConfig.WearState) && _community?.FindCommand != false;

    /// <summary>The catalog or community name of the identified model, when known.</summary>
    public string? ModelName => _model?.Name ?? _community?.Name;

    private bool ModelHasAnc => _community?.Anc switch
    {
        false => false,
        true => true,
        null => (_model?.NoiseLevels.Length ?? 0) > 0 || (_model?.AmbientLevels.Length ?? 0) > 0,
    };

    internal bool HasConfig(ushort id) => _confirmed.Contains(id);

    public IDeviceCapabilities Capabilities { get; }

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => !HasNoiseControl ? [] :
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
        .. HasAdaptiveAnc ? new[] { new NoiseControlOption(NoiseControlMode.Adaptive, null, "Adaptive", "NoiseMode_Adaptive", CapabilitySupport.Supported) } : [],
        .. NamedLevels(_model?.NoiseLevels ?? []).Select(n => new NoiseControlOption(NoiseControlMode.NoiseCancellation, $"nc-{n}", $"Level {n + 1}", LevelKey("Anc", n, _model!.NoiseLevels), CapabilitySupport.Supported)),
        .. NamedLevels(_model?.AmbientLevels ?? []).Select(n => new NoiseControlOption(NoiseControlMode.Transparency, $"ambient-{n}", $"Level {n + 1}", LevelKey("Ambient", n, _model!.AmbientLevels), CapabilitySupport.Supported)),
    ];

    /// <summary>
    /// Three-step lists are named strengths (ANC 0 balanced, 1 light, 2 deep; transparency 0 regular,
    /// 1 voice, 2 ambient) shown light → deep; longer lists are numbered steps 1…n.
    /// </summary>
    private static IEnumerable<byte> NamedLevels(byte[] levels) =>
        levels.All(l => l <= 2) ? new byte[] { 1, 0, 2 }.Where(levels.Contains) : levels;

    private static string LevelKey(string kind, byte level, byte[] levels) =>
        levels.All(l => l <= 2) ? $"Xiaomi_{kind}_{level}" : $"Xiaomi_Level_{level}";

    public IReadOnlyList<EqualizerPreset> EqualizerPresets => !HasEqualizer ? [] :
        _model!.Equalizers.Where(n => n != XiaomiCustomCurve.PresetId).Select(n => new EqualizerPreset($"xiaomi-{n}", $"EQ {n}", $"Xiaomi_Eq_{n}", CapabilitySupport.Supported)).ToArray();

    public IReadOnlyList<Guid> ControlServiceUuids { get; } = [XiaomiProtocol.FastConnectSppUuid, XiaomiProtocol.XiaoAiSppUuid, XiaomiProtocol.MmaGattServiceUuid];

    public int MatchScore(DeviceIdentity identity) =>
        Matches(identity.Name) ? _score : 0;

    /// <summary>True when <paramref name="name"/> contains one of this profile's name markers.</summary>
    public bool Matches(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _nameMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>This profile with low-latency mode declared (or not).</summary>
    public XiaomiDeviceProfile WithGamingMode(bool supported)
    {
        if (supported == HasGamingMode) return this;
        return new XiaomiDeviceProfile(ModelId, DisplayName, _nameMarkers, _score, supported, _model, _community, _confirmed);
    }

    internal XiaomiDeviceProfile ForDevice(string name)
    {
        var community = XiaomiCommunityModel.FindByName(name) ?? _community;
        var model = XiaomiModelFeatures.Find(name) ?? (community is not null ? XiaomiModelFeatures.Find(community.Name) : null) ?? _model;
        return new(ModelId, DisplayName, _nameMarkers, _score, HasGamingMode, model, community, _confirmed);
    }

    /// <summary>Identifies the model by the VID/PID from device info when the name did not.</summary>
    internal XiaomiDeviceProfile WithProductId(int vendorId, int productId)
    {
        if (_community is not null && _model is not null) return this;
        var community = _community ?? XiaomiCommunityModel.Find(vendorId, productId);
        if (community is null) return this;
        var model = _model ?? XiaomiModelFeatures.Find(community.Name);
        return new(ModelId, DisplayName, _nameMarkers, _score, HasGamingMode, model, community, _confirmed);
    }

    internal XiaomiDeviceProfile WithConfig(ushort id) => HasConfig(id) && (id != XiaomiConfig.LowLatency || HasGamingMode) ? this
        : new(ModelId, DisplayName, _nameMarkers, _score, HasGamingMode || id == XiaomiConfig.LowLatency, _model, _community, new HashSet<ushort>(_confirmed) { id });

    public IControllableProfile Remember(DeviceState known) =>
        WithGamingMode(HasGamingMode || known.Toggles.ContainsKey(DeviceToggle.GamingMode));

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new XiaomiDeviceController(channel, context, this);
}

/// <summary>Built-in Xiaomi-family profiles.</summary>
public static class XiaomiProfiles
{
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() => [XiaomiDeviceProfile.Xiaomi, XiaomiDeviceProfile.Redmi];
}
