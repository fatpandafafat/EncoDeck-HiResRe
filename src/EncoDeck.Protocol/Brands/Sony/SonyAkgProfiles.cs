// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Sony;

public static class SonyAkgProfiles
{
    public static readonly Guid PrimaryServiceUuid = new("956c7b26-d49a-4ba8-b03f-b17d393cb6e2");
    public static readonly Guid LegacyServiceUuid = new("96cc203e-5068-46ad-b32d-e316f5e069ba");

    /// <summary>
    /// MDR byte stream over BLE GATT (first writable / first notifying characteristic), used by
    /// mos9527/SonyHeadphonesClient PR #43 on a WH-1000XM6. Tried only after both RFCOMM services.
    /// </summary>
    public static readonly Guid BleServiceUuid = new("5b833e20-6bc7-4802-8e9a-723ceca4bd8f");

    /// <summary>HPC characteristics of <see cref="BleServiceUuid"/> (host → headset, headset → host), as listed by SonyPods.</summary>
    public static readonly Guid BleWriteUuid = new("5b833c60-6bc7-4802-8e9a-723ceca4bd8f"), BleNotifyUuid = new("5b833c61-6bc7-4802-8e9a-723ceca4bd8f");

    /// <summary>The two classic RFCOMM MDR services (not the GATT fallback).</summary>
    public static bool IsControlService(Guid uuid) => uuid == PrimaryServiceUuid || uuid == LegacyServiceUuid;

    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [
        .. SonyModelNames.All.Select(n => new SonyAkgProfile("sony", n,
            n.StartsWith("WF-", StringComparison.Ordinal) || n.StartsWith("LinkBuds", StringComparison.Ordinal))),
        .. new[] { "AKG N400NC", "AKG N700NCM2", "AKG N200NC", "AKG Y600" }
            .Select(n => new SonyAkgProfile("akg", n, n.Contains("N400", StringComparison.Ordinal))),
        // The original N700NC uses a different (Avnera) command layer over SPP; no public protocol
        // evidence was found, so it stays recognised-only.
        new AkgArtworkProfile("akg-n700nc", "AKG N700NC", false),
        new Akg.AkgHybridProfile(true),
        new Akg.AkgHybridProfile(false),
    ];
}

/// <summary>Recognized official model with artwork; no speculative protocol writes.</summary>
internal sealed class AkgArtworkProfile(string id, string name, bool earbuds) : IDeviceProfile
{
    public string ModelId => id;
    public string DisplayName => name;
    public string ImageKey => id;
    public DeviceForm Form => earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public IDeviceCapabilities Capabilities => DeviceCapabilities.Empty;
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    public IReadOnlyList<Guid> ControlServiceUuids => [];
    public int MatchScore(DeviceIdentity identity) => Normalize(identity.Name) == Normalize(name) ? 100 : 0;
    private static string Normalize(string? value) => new((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Sony MDR and AKG BES (N400NC family) profile. Every capability starts unknown and is opened only
/// after the device answered the corresponding readback.
/// </summary>
public sealed class SonyAkgProfile : IControllableProfile
{
    /// <summary>Transparency level ids: standard ambient sound and focus on voice (Sony), ambient aware and talk-through (AKG).</summary>
    public const string SonyAmbient = "sony-ambient", SonyVoice = "sony-voice", AkgAmbientAware = "akg-ambient", AkgTalkThrough = "akg-talkthru";

    private readonly CapabilitySupport _noiseSupport;
    private readonly IReadOnlyList<EqualizerPreset> _presets;
    private readonly ImmutableHashSet<DeviceToggle> _toggles;
    private readonly bool _ambientModes;

    internal SonyAkgProfile(string brand, string name, bool earbuds, CapabilitySupport noiseSupport = CapabilitySupport.Experimental,
        IReadOnlyList<EqualizerPreset>? presets = null, ImmutableHashSet<DeviceToggle>? toggles = null, bool ambientModes = false)
    {
        Brand = brand; DisplayName = name; Form = earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
        _noiseSupport = noiseSupport; _presets = presets ?? []; _toggles = toggles ?? []; _ambientModes = ambientModes;
    }

    private SonyAkgProfile With(CapabilitySupport? noise = null, IReadOnlyList<EqualizerPreset>? presets = null,
        ImmutableHashSet<DeviceToggle>? toggles = null, bool? ambientModes = null) =>
        new(Brand, DisplayName, Form == DeviceForm.Earbuds, noise ?? _noiseSupport, presets ?? _presets, toggles ?? _toggles, ambientModes ?? _ambientModes);

    public SonyAkgProfile WithNoiseSupport(bool available) => With(noise: available ? CapabilitySupport.Supported : CapabilitySupport.Unsupported);
    internal SonyAkgProfile WithEqualizers(IReadOnlyList<EqualizerPreset> presets) => With(presets: presets);
    internal SonyAkgProfile WithToggles(IEnumerable<DeviceToggle> confirmed) => With(toggles: confirmed.ToImmutableHashSet());
    internal SonyAkgProfile WithWearDetection(bool confirmed) => With(toggles: confirmed ? _toggles.Add(DeviceToggle.InEarDetection) : _toggles.Remove(DeviceToggle.InEarDetection));
    /// <summary>The device reported a layout with selectable ambient sound modes.</summary>
    internal SonyAkgProfile WithAmbientModes(bool available) => With(ambientModes: available);

    public IReadOnlyCollection<DeviceToggle> Toggles => _toggles;
    public string Brand { get; }
    public bool IsGeneric => false;
    public string ModelId => Brand + "-" + Normalize(DisplayName).Replace(Brand, "", StringComparison.Ordinal);
    public string DisplayName { get; }
    public string ImageKey => ModelId;
    public DeviceForm Form { get; }
    private bool Sony => Brand == "sony";
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, Sony ? "MDR battery query." : "BES device-info charge byte (confirmed on a real N400NC by tthayer/akg_earbud_control).")
        .Set(DeviceCapability.NoiseCancellation, _noiseSupport, "Enabled only after a successful live parameter query.")
        .Set(DeviceCapability.Equalizer, _presets.Count > 0 ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Preset list and current preset read from the device.")
        .Set(DeviceCapability.InEarDetection, _toggles.Contains(DeviceToggle.InEarDetection) ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "Enabled after live readback.")
        .Set(DeviceCapability.DualDeviceConnection, _toggles.Contains(DeviceToggle.DualDeviceConnection) ? CapabilitySupport.Supported : CapabilitySupport.Unknown, "MDR multipoint general setting; enabled after live readback.")
        .Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions
    {
        get
        {
            List<NoiseControlOption> options =
            [
                new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", _noiseSupport),
                new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", _noiseSupport),
            ];
            if (!_ambientModes) options.Add(new(NoiseControlMode.Transparency, null, "Ambient sound", "NoiseMode_Transparency", _noiseSupport));
            else if (Sony)
            {
                options.Add(new(NoiseControlMode.Transparency, SonyAmbient, "Ambient sound", "NoiseLevel_SonyAmbient", _noiseSupport));
                options.Add(new(NoiseControlMode.Transparency, SonyVoice, "Focus on voice", "NoiseLevel_SonyVoice", _noiseSupport));
            }
            else
            {
                options.Add(new(NoiseControlMode.Transparency, AkgAmbientAware, "Ambient aware", "NoiseLevel_AkgAmbientAware", _noiseSupport));
                options.Add(new(NoiseControlMode.Transparency, AkgTalkThrough, "TalkThru", "NoiseLevel_AkgTalkThrough", _noiseSupport));
            }
            return options;
        }
    }
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => _presets;

    /// <summary>
    /// Sony: both RFCOMM MDR services, then the MDR GATT fallback. AKG: the BES GATT service confirmed
    /// on a real N400NC first, then the classic SPP path EncoDeck used before (kept as a fallback).
    /// </summary>
    public IReadOnlyList<Guid> ControlServiceUuids => Sony
        ? [SonyAkgProfiles.PrimaryServiceUuid, SonyAkgProfiles.LegacyServiceUuid, SonyAkgProfiles.BleServiceUuid]
        : [AkgBleUuids.BesService, AkgBleUuids.Spp];

    public int MatchScore(DeviceIdentity identity)
    {
        string name = Normalize(identity.Name);
        string expected = Normalize(DisplayName);
        // Exact official model names only; protocol generation is probed on connection.
        return name == expected || (Brand == "sony" && name == "sony" + expected) ? 100 : 0;
    }
    private static string Normalize(string? name) => new((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => Sony
        ? new SonyMdrController(channel, context, this)
        : new AkgBesController(channel, context, this);
}
