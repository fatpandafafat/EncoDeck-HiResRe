// SPDX-License-Identifier: GPL-3.0-only
// Headband model traits (MOMENTUM 4 / 5, ACCENTUM, ACCENTUM Plus) follow Ariazonaa/momentum4-control
// (MIT, Copyright (c) 2026 Ariazonaa); the MIT notice is reproduced in THIRD_PARTY_NOTICES.md.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Sennheiser;

/// <summary>Per-model command families worth probing. A trait is never support by itself: every control opens only after a valid live read.</summary>
[Flags]
internal enum SennheiserTraits
{
    None = 0,
    /// <summary>Headband level endpoint <c>0x1A02</c>/<c>0x1A03</c> (0 = ANC, 100 = transparency); fallback when the status reads fail.</summary>
    Slider = 1 << 0,
    /// <summary>Independent ANC (<c>0x1A04</c>/<c>0x1A05</c>) and transparent hearing (<c>0x1804</c>/<c>0x1805</c>) statuses.</summary>
    StatusNoise = 1 << 1,
    /// <summary>Headband firmware: transparent hearing is only heard while ANC is on, and ANC off leaves its flag set.</summary>
    TransparencyNeedsAnc = 1 << 2,
    AntiWind = 1 << 3,
    Adaptive = 1 << 4,
    SmartPause = 1 << 5,
    LowLatency = 1 << 6,
    Gestures = 1 << 7,
    /// <summary>Feature 8 user EQ (band gains in 0.1 dB) with feature 4 sound mode; firmware 3.x and later.</summary>
    Equalizer = 1 << 8,
    /// <summary>Sound mode 2 (podcast / speech) is a selectable preset.</summary>
    Podcast = 1 << 9,
    /// <summary>Feature 10 paired-host list, addressed by index only.</summary>
    PairedDevices = 1 << 10,
    /// <summary><c>0x1402</c> connect of a listed host; the disconnect / delete commands are never sent.</summary>
    ConnectPaired = 1 << 11,
    /// <summary>Feature 22 per-earbud ring.</summary>
    Find = 1 << 12,
}

/// <summary>What the device actually answered in this session.</summary>
internal sealed record SennheiserLive(
    bool Noise = false, bool StatusNoise = false, bool Wind = false, bool Adaptive = false,
    bool SmartPause = false, bool LowLatency = false, bool Gestures = false,
    BandEqualizerSpec? Equalizer = null, bool PairedDevices = false, bool Find = false);

public static class SennheiserProfiles
{
    private const SennheiserTraits Headband = SennheiserTraits.StatusNoise | SennheiserTraits.Equalizer | SennheiserTraits.PairedDevices;
    private const SennheiserTraits Momentum = Headband | SennheiserTraits.TransparencyNeedsAnc | SennheiserTraits.AntiWind
        | SennheiserTraits.Adaptive | SennheiserTraits.SmartPause | SennheiserTraits.Podcast;

    public static IReadOnlyList<IDeviceProfile> Create() =>
    [new SennheiserProfile("m4", "MOMENTUM 4", false, Momentum | SennheiserTraits.Slider | SennheiserTraits.ConnectPaired, ["MOMENTUM 4 Wireless"]),
     new SennheiserProfile("m5", "MOMENTUM 5", false, Momentum, ["MOMENTUM 5 Wireless"]),
     // ACCENTUM's ANC-mode table reads all zero and ignores writes; no EQ write evidence.
     new SennheiserProfile("ac1", "ACCENTUM", false, SennheiserTraits.Slider | SennheiserTraits.StatusNoise | SennheiserTraits.PairedDevices, ["ACCENTUM Wireless"]),
     new SennheiserProfile("acp1", "ACCENTUM Plus", false, Headband | SennheiserTraits.Slider | SennheiserTraits.AntiWind | SennheiserTraits.Adaptive | SennheiserTraits.SmartPause, ["ACCENTUM Plus Wireless"]),
     new SennheiserProfile("hdb630", "HDB 630", false, Headband | SennheiserTraits.TransparencyNeedsAnc | SennheiserTraits.AntiWind
         | SennheiserTraits.Adaptive | SennheiserTraits.SmartPause, []),
     new SennheiserProfile("tw3", "MOMENTUM TW 3", true, SennheiserTraits.None, ["MOMENTUM True Wireless 3"]),
     new SennheiserProfile("tw4", "MOMENTUM TW 4", true, SennheiserTraits.StatusNoise | SennheiserTraits.AntiWind | SennheiserTraits.SmartPause
         | SennheiserTraits.LowLatency | SennheiserTraits.Gestures | SennheiserTraits.Equalizer | SennheiserTraits.PairedDevices | SennheiserTraits.Find,
         ["MOMENTUM TW4", "MOMENTUM True Wireless 4"]),
     new SennheiserProfile("atw1", "ACCENTUM True Wireless", true, SennheiserTraits.None, ["ACCENTUM TW"]),
     new SennheiserProfile("msport1", "MOMENTUM Sport", true, SennheiserTraits.None, []),
     new SennheiserProfile("cx200", "CX True Wireless", true, SennheiserTraits.None, []),
     new SennheiserProfile("cx200sports", "SPORT True Wireless", true, SennheiserTraits.None, []),
     new SennheiserProfile("cx400ii", "CX Plus True Wireless", true, SennheiserTraits.None, ["CX Plus SE True Wireless"]),
     new SennheiserProfile("cx500", "CX 500", true, SennheiserTraits.None, [])];
}

public sealed class SennheiserProfile : IControllableProfile, IBandEqualizerProfile
{
    private readonly string _code, _name;
    private readonly bool _earbuds;
    private readonly string[] _aliases;
    private readonly SennheiserLive _live;

    internal SennheiserProfile(string code, string name, bool earbuds, SennheiserTraits traits, string[] aliases, SennheiserLive? live = null)
    {
        _code = code; _name = name; _earbuds = earbuds; Traits = traits; _aliases = aliases; _live = live ?? new();
    }

    public static Guid GattServiceId { get; } = new("63331337-23c1-11e5-b696-feff819cdc9f");
    internal SennheiserTraits Traits { get; }
    public string Brand => "sennheiser";
    public string ModelId => "sennheiser-" + _code;
    public string DisplayName => _name;
    public string ImageKey => ModelId;
    public DeviceForm Form => _earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public bool IsGeneric => false;
    public bool IsTw4 => _code == "tw4";
    /// <summary>A listed host can be connected (never disconnected or removed: those commands can clear pairings).</summary>
    public bool CanConnectPairedDevices => _live.PairedDevices && Traits.HasFlag(SennheiserTraits.ConnectPaired);
    public BandEqualizerSpec? BandEqualizer => _live.Equalizer;
    internal SennheiserProfile WithLive(SennheiserLive live) => live == _live ? this : new(_code, _name, _earbuds, Traits, _aliases, live);

    /// <summary>Notification features registered with <c>0x0007</c>, in order.</summary>
    internal IReadOnlyList<byte> NotificationFeatures
    {
        get
        {
            bool noise = Traits.HasFlag(SennheiserTraits.StatusNoise);
            var features = new List<byte> { 3 };
            if (Traits.HasFlag(SennheiserTraits.Equalizer) || Traits.HasFlag(SennheiserTraits.LowLatency)) features.Add(4);
            if (Traits.HasFlag(SennheiserTraits.Equalizer)) features.Add(8);
            if (Traits.HasFlag(SennheiserTraits.PairedDevices)) features.Add(10);
            if (noise) features.Add(12);
            if (noise || Traits.HasFlag(SennheiserTraits.Slider)) features.Add(13);
            if (Traits.HasFlag(SennheiserTraits.Find)) features.Add(22);
            return features;
        }
    }

    public IReadOnlyCollection<DeviceToggle> Toggles =>
        [.. _live.SmartPause ? new[] { DeviceToggle.InEarDetection } : [], .. _live.LowLatency ? new[] { DeviceToggle.GamingMode } : []];

    private static CapabilitySupport Live(bool ready) => ready ? CapabilitySupport.Supported : CapabilitySupport.Unknown;

    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "GAIA v3 battery query 0x0603.")
        .Set(DeviceCapability.InEarDetection, Live(_live.SmartPause), "Smart Pause 0x080C/0x080D; valid live readback required.")
        .Set(DeviceCapability.GamingMode, Live(_live.LowLatency), "TW4 device-side low latency; valid live readback required.")
        .Set(DeviceCapability.TouchCustomization, Live(_live.Gestures), "TW4 per-ear MMI mappings; only read-back slots are writable.")
        .Set(DeviceCapability.NoiseCancellation, Live(_live.Noise),
            _live.StatusNoise ? "Independent ANC and transparent-hearing statuses, confirmed by readback."
                : "Headband transparency endpoints only after valid live readback; no ANC-off command is inferred.")
        .Set(DeviceCapability.NoiseCancellationLevel, Live(_live.Wind), "Anti-wind entry of the ANC mode table (0x1A00/0x1A01).")
        .Set(DeviceCapability.AdaptiveNoiseCancellation, Live(_live.Adaptive), "Adaptive entry of the ANC mode table (0x1A00/0x1A01).")
        .Set(DeviceCapability.Equalizer, Live(_live.Equalizer is not null), "Sound mode 0x0803/0x0804; firmware 3.x and later.")
        .Set(DeviceCapability.CustomEqualizer, Live(_live.Equalizer is not null), "User EQ band gains 0x1001/0x1003 in 0.1 dB, confirmed by readback.")
        .Set(DeviceCapability.DualDeviceConnection, Live(_live.PairedDevices), "Paired-host list 0x1400/0x1401; connect only where verified.")
        .Set(DeviceCapability.FindMyEarbuds, Live(_live.Find), "TW4 per-earbud ring 0x2C02/0x2C01 with state query 0x2C03.")
        .Build();

    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => !_live.Noise ? [] :
    [.. _live.StatusNoise ? new[] { new NoiseControlOption(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported) } : [],
     new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
     .. _live.Wind ? new[]
     {
         new NoiseControlOption(NoiseControlMode.NoiseCancellation, "normal", "Normal", "Sennheiser_NormalAnc", CapabilitySupport.Supported),
         new NoiseControlOption(NoiseControlMode.NoiseCancellation, "anti-wind", "Wind noise reduction", "NoiseLevel_AntiWind", CapabilitySupport.Supported),
     } : [],
     .. _live.Adaptive ? new[] { new NoiseControlOption(NoiseControlMode.Adaptive, null, "Adaptive", "NoiseMode_Adaptive", CapabilitySupport.Supported) } : [],
     new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported)];

    /// <summary>Sound modes other than the user curve; the curve itself is the single custom EQ entry.</summary>
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => _live.Equalizer is null ? [] :
    [new(SoundModePreset(0), "Off", "Sennheiser_EqOff", CapabilitySupport.Supported),
     .. Traits.HasFlag(SennheiserTraits.Podcast) ? new[] { new EqualizerPreset(SoundModePreset(2), "Podcast", "Sennheiser_EqPodcast", CapabilitySupport.Supported) } : []];

    internal static string SoundModePreset(int mode) => "sennheiser-sound-" + mode;
    public IReadOnlyList<Guid> ControlServiceUuids => [new("a2129ff3-081b-4c45-8afe-469d9c4842ec"), GattServiceId];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Bluetooth) return 0;
        var actual = Normalize(identity.Name);
        return new[] { _name }.Concat(_aliases).Any(n => actual == Normalize(n) || actual == Normalize("Sennheiser " + n)) ? 100 : 0;
    }
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new SennheiserController(channel, context, this, _earbuds);
}
