// SPDX-License-Identifier: GPL-3.0-only
// Model data and ANC mappings ported from MiuixMoondrop MoondropModels.kt (GPL-3.0-or-later).
// EDGE 2 / ROBIN / NEKOCAKE / U.C.T.S entries and the PEQ gate follow bqj6666/FxxkMoondrop (GPL-3.0),
// FEAKEuser/moondrop-control (GPL-3.0-or-later) and Konakonai/Edge2Pods (MIT, Copyright (c) 2026 Konakonai;
// the MIT notice is reproduced in THIRD_PARTY_NOTICES.md).
using System.Text;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Moondrop;

public static class MoondropConstants
{
    public static readonly Guid GattServiceId = new("00001100-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid GattCommandId = new("00001101-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid GattResponseId = new("00001102-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid GattDataId = new("00001103-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid SppServiceId = new("00001101-0000-1000-8000-00805f9b34fb");
}

public static class MoondropProfiles
{
    private static readonly IReadOnlyList<Guid> Ble = [MoondropConstants.GattServiceId];
    private static readonly IReadOnlyList<Guid> Spp = [MoondropConstants.SppServiceId];
    private static readonly IReadOnlyList<Guid> BleThenSpp = [MoondropConstants.GattServiceId, MoondropConstants.SppServiceId];
    private static readonly IReadOnlyList<Guid> SppThenBle = [MoondropConstants.SppServiceId, MoondropConstants.GattServiceId];
    private static readonly byte[] AcThreeSet = [1, 2, 4];
    private static readonly byte[] AcThreeGet = [0, 1, 2];
    private static readonly byte[] Ga2Set = [1, 2, 4, 3];
    private static readonly byte[] Ga2Get = [0, 1, 2, 3];
    private static readonly byte[] AcIdentity = [1, 2, 3, 4];
    private static readonly byte[] PuddingMap = [0, 4, 2, 3, 1];
    private static readonly byte[] AncV2Identity = [0, 1, 2, 3, 4];

    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        // Only these four model entries are marked verified by the reference catalog.
        new MoondropProfile(new("edge", "MOONDROP EDGE", ["MOONDROP EDGE", "EDGE", "羽翼 EDGE"],
            SppThenBle, true, 8, AcThreeSet, AcThreeGet, SingleDeviceBattery: true, Lhdc: true, PromptVolume: true, DualConnection: true, Form: DeviceForm.Headphones,
            RfcommChannel: 1, PeqVerified: true)),
        new MoondropProfile(new("pudding", "MOONDROP PUDDING", ["PUDDING", "MD-TWS-056", "布丁"],
            Spp, true, 32, PuddingMap, PuddingMap, Lhdc: true, PromptVolume: true, DualConnection: true)),
        new MoondropProfile(new("golden_ages_2", "MOONDROP GOLDEN AGES 2", ["GOLDEN AGES 2", "GOLDENAGES2", "GOLDEN AGES2", "梦回2", "GA2"],
            Ble, true, 8, Ga2Set, Ga2Get, Lhdc: true)),
        new MoondropProfile(new("space_travel_2", "MOONDROP SPACE TRAVEL 2", ["SPACE TRAVEL 2", "SPACETRAVEL2", "太空漫游2", "ST2"],
            BleThenSpp, true, 8, Ga2Set, Ga2Get, PeqVerified: true)),
        new MoondropProfile(new("golden_ages", "MOONDROP GOLDEN AGES", ["GOLDEN AGES", "GOLDENAGES", "梦回", "梦回1979"],
            Ble, false, 8, Ga2Set, Ga2Get, Lhdc: true)),
        new MoondropProfile(new("moca", "MOONDROP MOCA", ["MOCA", "猫咖"],
            BleThenSpp, false, 8, AcIdentity, null)),
        // Real NEKOCAKE 1.0.0 answers a GAIA subset (features 0/1/3/5/6) on RFCOMM channel 1; no battery or ANC replies.
        new MoondropProfile(new("nekocake", "MOONDROP NEKOCAKE", ["NEKOCAKE", "NEKO CAKE", "猫饼"],
            SppThenBle, false, null, AcIdentity, null, RfcommChannel: 1)),
        new MoondropProfile(new("pill", "MOONDROP MUSIC PILL", ["MUSIC PILL", "PILL", "音乐胶囊"],
            Ble, false, 8, AcIdentity, null)),
        new MoondropProfile(new("ultrasonic", "MOONDROP ULTRASONIC", ["ULTRASONIC", "超声波"],
            Ble, false, 8, AcIdentity, null)),
        // A user log reports only states 1 / 2 (ANC / transparency); the published ROBIN map writes 1 / 2 / 4 like EDGE.
        new MoondropProfile(new("robin", "MOONDROP ROBIN", ["ROBIN", "知更鸟", "ROBIN'S EARPHONES"],
            Ble, false, 8, AcThreeSet, AcThreeGet)),
        new MoondropProfile(new("alice", "MOONDROP ALICE", ["ALICE", "爱丽丝"],
            Ble, false, 8, AcIdentity, null, Lhdc: true)),
        new MoondropProfile(new("sparks", "MOONDROP SPARKS", ["SPARKS", "火花"],
            Ble, false, 8, AcIdentity, null, Lhdc: true)),
        new MoondropProfile(new("voyager", "MOONDROP VOYAGER", ["VOYAGER", "旅行者"],
            Ble, false, 8, AcIdentity, null, Lhdc: true, Form: DeviceForm.Headphones)),
        new MoondropProfile(new("block", "MOONDROP BLOCK", ["BLOCK", "方糖"],
            Ble, false, 8, AcIdentity, null)),
        new MoondropProfile(new("space_travel", "MOONDROP SPACE TRAVEL", ["SPACE TRAVEL", "SPACETRAVEL", "太空漫游"],
            Ble, false, 8, AcIdentity, null)),
        // EDGE 2 1.2.5: classic RFCOMM channel 1 only, ANC V2 (feature 32) 0 off / 1 ANC / 2 transparency / 3 anti-wind / 4 adaptive.
        new MoondropProfile(new("edge2", "MOONDROP EDGE2", ["MOONDROP EDGE2", "EDGE2", "EDGE 2", "羽翼 EDGE2"],
            Spp, false, 32, AncV2Identity, AncV2Identity, Lhdc: true, PromptVolume: true, DualConnection: true, Form: DeviceForm.Headphones, RfcommChannel: 1)),
        // U.C.T.S (open-ear, MD-OWS-014): a user confirmed GAIA recognition and per-ear battery; the hardware has no ANC.
        new MoondropProfile(new("ucts", "MOONDROP U.C.T.S", ["U.C.T.S", "UCTS", "MD-OWS-014"],
            BleThenSpp, false, null, AcIdentity, null)),
        new MoondropProfile(new("space_travel_2_ultra", "MOONDROP SPACE TRAVEL 2 ULTRA", ["SPACE TRAVEL 2 ULTRA", "SPACETRAVEL2ULTRA", "太空漫游2 ULTRA", "ST2 ULTRA"],
            Ble, false, 8, Ga2Set, Ga2Get)),
        // 9ECA is a separate, unimplemented protocol, never an alternative GAIA service.
        new MoondropProfile(new("generic", "MOONDROP headphones", [],
            BleThenSpp, false, null, AcIdentity, null)),
    ];

    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var result = new StringBuilder(name.Length);
        foreach (char c in name.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) result.Append(c);
        return result.ToString();
    }
}

internal sealed record MoondropModel(
    string Id,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<Guid> Services,
    bool Verified,
    int? AncFeature,
    byte[] AncSetMap,
    byte[]? AncGetMap,
    bool SingleDeviceBattery = false,
    bool Lhdc = false,
    bool PromptVolume = false,
    bool DualConnection = false,
    DeviceForm Form = DeviceForm.Earbuds,
    uint? RfcommChannel = null,
    bool PeqVerified = false);

/// <summary>Model hints are not proof: inferred ANC mappings remain experimental after feature discovery.</summary>
/// <summary>What a valid feature-5 read reported: factory preset ids and whether the user curve (0x3F) exists.</summary>
internal sealed record MoondropEqualizerInfo(IReadOnlyList<byte> FactoryPresets, bool UserCurve, BandEqualizerSpec? Curve)
{
    public bool Equals(MoondropEqualizerInfo? other) => other is not null && FactoryPresets.SequenceEqual(other.FactoryPresets)
        && UserCurve == other.UserCurve && Curve == other.Curve;
    public override int GetHashCode() => HashCode.Combine(FactoryPresets.Count, UserCurve, Curve);
}

public sealed class MoondropProfile : IControllableProfile, IBandEqualizerProfile
{
    private readonly MoondropModel _model;
    private readonly IReadOnlySet<int>? _features;
    private readonly MoondropEqualizerInfo? _equalizer;
    private readonly byte[] _setMap;
    private readonly byte[]? _getMap;
    private readonly CapabilitySupport _noiseSupport;
    private readonly byte[]? _gainMap;

    private readonly IReadOnlySet<DeviceToggle> _codecs;

    internal MoondropProfile(MoondropModel model, IReadOnlySet<int>? features = null, IReadOnlySet<DeviceToggle>? codecs = null, MoondropEqualizerInfo? equalizer = null)
    {
        _codecs = codecs ?? new HashSet<DeviceToggle>();
        _model = model;
        _features = features is null ? null : new HashSet<int>(features);
        // The EQ is only offered after the device returned a complete preset list or user curve.
        _equalizer = _features?.Contains(5) == true ? equalizer : null;
        var eqSupport = _equalizer is null ? (_features is not null && !_features.Contains(5) ? CapabilitySupport.Unsupported : CapabilitySupport.Unknown)
            : model.PeqVerified ? CapabilitySupport.Supported : CapabilitySupport.Experimental;
        // Only catalogued mappings are meaningful; discovery cannot identify an unknown model's gain order.
        _gainMap = model.Id switch
        {
            "pudding" or "golden_ages_2" or "space_travel_2" or "golden_ages" or "space_travel_2_ultra" => [2, 1, 0],
            "edge" or "moca" or "nekocake" or "pill" or "ultrasonic" or "robin" or "alice" or "sparks" or "voyager" or "edge2" or "space_travel" => [0, 1, 2],
            _ => null,
        };
        AncFeature = features is null ? model.AncFeature
            : model.AncFeature is int known && features.Contains(known) ? known
            : features.Contains(8) ? 8 : features.Contains(32) ? 32 : features.Contains(2) ? 2 : null;
        // Model-specific SET/GET differences apply only to that model's known feature path.
        bool knownPath = AncFeature == model.AncFeature;
        _setMap = knownPath ? model.AncSetMap : AncFeature == 32 ? [0, 1, 2, 3, 4, 5]
            : AncFeature == 2 ? [0, 1] : [1, 2, 3, 4];
        _getMap = knownPath ? model.AncGetMap : null;
        _noiseSupport = AncFeature is null ? (features is null ? CapabilitySupport.Unknown : CapabilitySupport.Unsupported)
            : model.Verified && knownPath ? CapabilitySupport.Supported : CapabilitySupport.Experimental;
        NoiseControlOptions = CreateNoiseOptions(_setMap.Length, _noiseSupport);
        Capabilities = DeviceCapabilities.CreateBuilder()
            .Set(DeviceCapability.BatteryLevel, FeatureSupport(13, model.Verified, true), Evidence("GAIA battery feature 13"))
            .Set(DeviceCapability.NoiseCancellation, _noiseSupport, Evidence("Model-specific ANC GET and SET mappings"))
            .Set(DeviceCapability.AdaptiveNoiseCancellation, _setMap.Length >= 5 ? _noiseSupport : CapabilitySupport.Unsupported, Evidence("ANC mode table"))
            .Set(DeviceCapability.NoiseCancellationLevel, _setMap.Length >= 4 ? _noiseSupport : CapabilitySupport.Unsupported, Evidence("Anti-wind ANC mode"))
            .Set(DeviceCapability.PromptVolume, FeatureSupport(14, model.Id == "pudding", model.PromptVolume), Evidence("GAIA voice configuration; percentage volume"))
            .Set(DeviceCapability.DualDeviceConnection, FeatureSupport(20, model.Id is "edge" or "pudding", model.DualConnection), Evidence("GAIA ONEBRINGTWO"))
            .Set(DeviceCapability.Gain, _gainMap is null ? CapabilitySupport.Unsupported : DiscoveredSupport(15, model.Verified), Evidence("Catalogued UI low/mid/high to DAC_GAIN device mapping"))
            .Set(DeviceCapability.IndicatorLight, DiscoveredSupport(19, model.Id == "pudding"), Evidence("GAIA LED GET/SET 1/2"))
            .Set(DeviceCapability.PromptTone, DiscoveredSupport(14, model.Id == "pudding"), Evidence("Complete three-byte VoiceConf read-modify-write"))
            .Set(DeviceCapability.Equalizer, _equalizer is null ? eqSupport : _equalizer.FactoryPresets.Count > 0 ? eqSupport : CapabilitySupport.Unsupported,
                Evidence("GAIA Music Processing (feature 5) factory presets, confirmed by readback of the active preset"))
            .Set(DeviceCapability.CustomEqualizer, _equalizer is null ? eqSupport : _equalizer is { UserCurve: true, Curve: not null } ? eqSupport : CapabilitySupport.Unsupported,
                Evidence("Feature 5 user PEQ curve (frequency / Q / filter / gain per band); gains written and confirmed by byte-exact readback"))
            .Set(DeviceCapability.SpatialAudio, DiscoveredSupport(18, false), Evidence("GAIA SPATIAL_AUDIO GET/SET 1/2; protocol implementation, not hardware-verified"))
            .Set(DeviceCapability.HeadTracking, DiscoveredSupport(18, false), Evidence("GAIA SPATIAL_AUDIO head-tracking GET/SET 3/4; protocol implementation, not hardware-verified"))
            .Set(DeviceCapability.InEarDetection, DiscoveredSupport(17, false), Evidence("GAIA LIGHT_SENSOR GET/SET 1/2; protocol implementation, not hardware-verified"))
            .Set(DeviceCapability.BassEngine, DiscoveredSupport(27, false), Evidence("GAIA DYBASS GET/SET 1/2; protocol implementation, not hardware-verified"))
            .Set(DeviceCapability.ChannelSwap, DiscoveredSupport(30, false), Evidence("GAIA LR_CHANNEL GET/SET 1/2; protocol implementation, not hardware-verified"))
            .Set(DeviceCapability.HiResCodec, CodecSupport(DeviceToggle.HiRes), Evidence("GAIA CODEC_TYPE LHDC GET/SET 5/6; enabled only after a valid LHDC response"))
            .Set(DeviceCapability.Ldac, CodecSupport(DeviceToggle.Ldac), Evidence("GAIA CODEC_TYPE LDAC GET/SET 2/4; enabled only after a valid LDAC response"))
            .Set(DeviceCapability.Lc3, CodecSupport(DeviceToggle.Lc3), Evidence("GAIA CODEC_TYPE LC3 GET/SET 1/3; enabled only after a valid LC3 response"))
            .Set(DeviceCapability.TouchCustomization, DiscoveredSupport(22, model.Id == "pudding"), Evidence("Five-byte TOUCHV2 both-ear configuration; action IDs 4/5 are inferred"))
            .Build();
        Toggles = features is null ? [] :
        [
            .. features.Contains(16) ? _codecs.ToArray() : [],
            .. features.Contains(20) ? new[] { DeviceToggle.DualDeviceConnection } : [],
            .. features.Contains(19) ? new[] { DeviceToggle.IndicatorLight } : [],
            .. features.Contains(14) ? new[] { DeviceToggle.PromptTone } : [],
            .. features.Contains(17) ? new[] { DeviceToggle.InEarDetection } : [],
            .. features.Contains(18) ? new[] { DeviceToggle.SpatialAudio, DeviceToggle.HeadTracking } : [],
            .. features.Contains(27) ? new[] { DeviceToggle.BassEngine } : [],
            .. features.Contains(30) ? new[] { DeviceToggle.ChannelSwap } : [],
        ];
        ProfileKey = ModelId + (features is null ? string.Empty : "/" + string.Join(',', features.Order()))
            + (_equalizer is null ? string.Empty : $"/eq:{string.Join(',', _equalizer.FactoryPresets)}:{_equalizer.UserCurve}:{_equalizer.Curve}");
        EqualizerPresets = _equalizer is null || eqSupport is CapabilitySupport.Unknown or CapabilitySupport.Unsupported ? [] :
            [.. _equalizer.FactoryPresets.Select(id => new EqualizerPreset(FactoryPresetId(id), $"Preset {id + 1}", $"Eq_DevicePreset:{id + 1}", eqSupport))];
    }

    public string Brand => "moondrop";
    public bool IsGeneric => _model.Id == "generic";
    public string ModelId => "moondrop-" + _model.Id.Replace('_', '-');
    public string DisplayName => _model.Name;
    public string ImageKey => ModelId;
    public DeviceForm Form => _model.Form;
    public bool Verified => _model.Verified;
    public bool SingleDeviceBattery => _model.SingleDeviceBattery;
    public CapabilitySupport LhdcSupport => FeatureSupport(16, _model.Id == "pudding", _model.Lhdc);
    public int? AncFeature { get; }
    public string ProfileKey { get; }
    public IDeviceCapabilities Capabilities { get; }
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions { get; }
    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; }
    public BandEqualizerSpec? BandEqualizer => _equalizer?.UserCurve == true ? _equalizer.Curve : null;
    internal static string FactoryPresetId(byte id) => "moondrop-eq-" + id;
    public IReadOnlyList<Guid> ControlServiceUuids => _model.Services;
    /// <summary>Known RFCOMM channel (EDGE: confirmed by Windows firmware query; EDGE 2 / NEKOCAKE: community hardware logs); never a channel scan.</summary>
    public uint? RfcommChannel => _model.RfcommChannel;
    public IReadOnlyCollection<DeviceToggle> Toggles { get; }
    public static DeviceCapability ToggleCapability(DeviceToggle toggle) => toggle switch
    {
        DeviceToggle.InEarDetection => DeviceCapability.InEarDetection,
        DeviceToggle.BassEngine => DeviceCapability.BassEngine,
        DeviceToggle.ChannelSwap => DeviceCapability.ChannelSwap,
        DeviceToggle.SpatialAudio => DeviceCapability.SpatialAudio,
        DeviceToggle.HeadTracking => DeviceCapability.HeadTracking,
        DeviceToggle.IndicatorLight => DeviceCapability.IndicatorLight,
        DeviceToggle.PromptTone => DeviceCapability.PromptTone,
        DeviceToggle.Ldac => DeviceCapability.Ldac,
        DeviceToggle.Lc3 => DeviceCapability.Lc3,
        DeviceToggle.HiRes => DeviceCapability.HiResCodec,
        _ => DeviceCapability.DualDeviceConnection,
    };
    public byte? EncodeGain(int level) => _gainMap is not null && (uint)level < (uint)_gainMap.Length ? _gainMap[level] : null;
    public int? DecodeGain(byte value) => _gainMap is not null && Array.IndexOf(_gainMap, value) is var level && level >= 0 ? level : null;
    public bool HasFeature(int feature) => _features?.Contains(feature) == true;

    /// <summary>GAIA features the catalog implies for this model; what a full discovery would report.</summary>
    internal IReadOnlySet<int> CatalogFeatures()
    {
        var features = new HashSet<int> { 0, 13 };
        if (_model.AncFeature is { } anc) features.Add(anc);
        if (_gainMap is not null) features.Add(15);
        if (_model.Lhdc) features.Add(16);
        if (_model.PromptVolume) features.Add(14);
        if (_model.DualConnection) features.Add(20);
        if (_model.Id == "pudding") features.UnionWith([19, 22]);
        return features;
    }

    private CapabilitySupport DiscoveredSupport(int feature, bool confirmed) => _features is null ? CapabilitySupport.Unknown
        : !_features.Contains(feature) ? CapabilitySupport.Unsupported
        : confirmed ? CapabilitySupport.Supported : CapabilitySupport.Experimental;

    private CapabilitySupport CodecSupport(DeviceToggle codec) => codec == DeviceToggle.HiRes ? LhdcSupport : DiscoveredSupport(16, false);

    public MoondropProfile WithFeatures(IReadOnlySet<int> features) =>
        _features is not null && _features.SetEquals(features) ? this : new(_model, features, _codecs, _equalizer);

    public MoondropProfile WithCodec(DeviceToggle codec)
    {
        if (codec is not (DeviceToggle.HiRes or DeviceToggle.Ldac or DeviceToggle.Lc3) || _codecs.Contains(codec)) return this;
        var codecs = new HashSet<DeviceToggle>(_codecs) { codec };
        return new(_model, _features, codecs, _equalizer);
    }

    internal MoondropProfile WithEqualizer(MoondropEqualizerInfo? equalizer) =>
        _features is null || Equals(_equalizer, equalizer) ? this : new(_model, _features, _codecs, equalizer);

    public NoiseControlState? DecodeNoise(byte value)
    {
        if (AncFeature is null) return null;
        if (AncFeature == 2) return value <= 1 ? NoiseControlOptions[value].ToState() : null;
        int index = _getMap is null ? Array.IndexOf(_setMap, value)
            : value < _getMap.Length ? _getMap[value] : -1;
        return index >= 0 && index < NoiseControlOptions.Count ? NoiseControlOptions[index].ToState() : null;
    }

    public byte? EncodeNoise(NoiseControlState state)
    {
        if (AncFeature is null) return null;
        for (int i = 0; i < NoiseControlOptions.Count; i++)
            if (NoiseControlOptions[i].Matches(state)) return _setMap[i];
        return null;
    }

    public int MatchScore(DeviceIdentity identity)
    {
        string name = MoondropProfiles.Normalize(identity.Name);
        bool branded = name.Contains("moondrop", StringComparison.Ordinal) || name.Contains("水月雨", StringComparison.Ordinal);
        if (IsGeneric) return branded ? 10 : 0;
        string modelName = name.Replace("moondrop", "", StringComparison.Ordinal).Replace("水月雨", "", StringComparison.Ordinal);
        int longest = 0;
        foreach (string alias in _model.Aliases)
        {
            string key = MoondropProfiles.Normalize(alias).Replace("moondrop", "", StringComparison.Ordinal);
            // Bare generic words such as EDGE, BLOCK or ALICE must match the entire device name.
            if (modelName == key || (branded && modelName.Contains(key, StringComparison.Ordinal)))
                longest = Math.Max(longest, key.Length);
        }
        return longest == 0 ? 0 : 100 + longest;
    }

    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) =>
        new MoondropDeviceController(channel, context, this);

    private CapabilitySupport FeatureSupport(int feature, bool confirmed, bool hint)
    {
        if (_features is not null)
            return !_features.Contains(feature) ? CapabilitySupport.Unsupported
                : confirmed ? CapabilitySupport.Supported : CapabilitySupport.Experimental;
        if (confirmed) return CapabilitySupport.Supported;
        return hint && !IsGeneric ? CapabilitySupport.Experimental : CapabilitySupport.Unknown;
    }

    private string Evidence(string detail) => _model.Verified
        ? $"MiuixMoondrop model catalog (hardware-verified model); {detail}. Individual features require device discovery."
        : $"MiuixMoondrop chip-level inference, not hardware-verified for this model; {detail}.";

    private static IReadOnlyList<NoiseControlOption> CreateNoiseOptions(int count, CapabilitySupport support)
    {
        NoiseControlOption[] options =
        [
            new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", support),
            new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", support),
            new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", support),
            new(NoiseControlMode.NoiseCancellation, "anti-wind", "Anti-wind", "NoiseLevel_AntiWind", support),
            new(NoiseControlMode.Adaptive, null, "Adaptive", "NoiseMode_Adaptive", support),
            new(NoiseControlMode.Transparency, "live", "Live", "", support),
        ];
        return options[..count];
    }
}
