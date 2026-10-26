// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>Official product/driver model names. Recognition does not imply a vendor control protocol.</summary>
public static class UsbHeadsetProfiles
{
    // VH800PRO.Inf from Rapoo Headset Control 1.0.0.2. Never generalize this PID to other models.
    public static UsbHeadsetProfile? MatchHardware(IEnumerable<string> hardwareIds)
    {
        foreach (string id in hardwareIds)
        {
            var match = Regex.Match(id, @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            ushort vendor = Convert.ToUInt16(match.Groups[1].Value, 16), product = Convert.ToUInt16(match.Groups[2].Value, 16);
            string? model = vendor == 0x24AE && product == 0x7205 ? "rapoo-usb-vh800-pro" :
                BarracudaXProtocol.IsReceiver(vendor, product) ? "razer-usb-barracuda-x" : UsbVendorBatteryProtocol.Model(vendor, product) ?? RecognizedOnly(vendor, product);
            if (model is not null) return Create().OfType<UsbHeadsetProfile>().Single(p => p.ModelId == model);
        }
        return null;
    }

    // BlackShark V3 / V3 Pro receivers (OpenRazer #2794) are named exactly, but the published battery
    // request differs between the driver and its notes, and frequent queries have reset the radio link.
    private static string? RecognizedOnly(ushort vendor, ushort product) => (vendor, product) switch
    {
        (0x1532, 0x057A) => "razer-usb-blackshark-v3",
        (0x1532, 0x0577) => "razer-usb-blackshark-v3-pro",
        _ => null,
    };

    public static IReadOnlyList<IDeviceProfile> Create() =>
    [
        new UsbHeadsetProfile("edifier", "g2-wireless", "HECATE G2 无线版", ["HECATE G2", "G2无线7.1版"]),
        new UsbHeadsetProfile("edifier", "g3-wireless", "HECATE G3 无线版", ["HECATE G3"]),
        new UsbHeadsetProfile("edifier", "g3-max", "HECATE G3 Max", ["HECATE G3 Max 无线版"]),
        new UsbHeadsetProfile("edifier", "g4s", "HECATE G4S", []),
        new UsbHeadsetProfile("edifier", "g4-max", "HECATE G4 Max", []),
        new UsbHeadsetProfile("edifier", "g5-wireless", "HECATE G5 无线版", ["HECATE G5"]),
        new UsbHeadsetProfile("edifier", "g5-max", "HECATE G5 Max", []),
        new UsbHeadsetProfile("dareu", "a710", "DAREU A710", ["A710-WIRELESS"]),
        new UsbHeadsetProfile("dareu", "a710x", "DAREU A710X", ["A710X-WIRELESS"]),
        new UsbHeadsetProfile("dareu", "a3-pro", "DAREU A3 Pro", []),
        new UsbHeadsetProfile("dareu", "a3-max", "DAREU A3 Max", []),
        new UsbHeadsetProfile("dareu", "a5-pro", "DAREU A5 Pro", []),
        new UsbHeadsetProfile("dareu", "a5-max", "DAREU A5 Max", []),
        new UsbHeadsetProfile("dareu", "eh925-ultra", "DAREU EH925 ULTRA", []),
        new UsbHeadsetProfile("dareu", "eh722x-ultra", "DAREU EH722X ULTRA", []),
        new UsbHeadsetProfile("rapoo", "vh800", "RAPOO VH800", ["VH800"]),
        new UsbHeadsetProfile("rapoo", "vh800-pro", "RAPOO VH800Pro", ["VH800Pro", "VH800 Pro"]),
        new UsbHeadsetProfile("rapoo", "vh850", "RAPOO VH850", ["VH850"]),
        new UsbHeadsetProfile("mchose", "x9", "MCHOSE X9", ["迈从 X9"]),
        new UsbHeadsetProfile("mchose", "v9", "MCHOSE V9", ["迈从 V9"]),
        new UsbHeadsetProfile("mchose", "v9-pro", "MCHOSE V9 Pro", ["迈从 V9 Pro"]),
        new UsbHeadsetProfile("mchose", "v9-turbo-plus", "MCHOSE V9 Turbo+", ["MCHOSE V9Turbo+", "迈从 V9 Turbo+"]),
        new UsbHeadsetProfile("mchose", "s9-pro", "MCHOSE S9 Pro", ["迈从 S9 Pro"]),
        new UsbHeadsetProfile("mchose", "g9-pro", "MCHOSE G9 Pro", ["迈从 G9 Pro"]),
        new UsbHeadsetProfile("mchose", "r9", "MCHOSE R9", ["迈从 R9"]),
        new UsbHeadsetProfile("razer", "blackshark-v2-pro-2020", "Razer BlackShark V2 Pro (2020)", []),
        new UsbHeadsetProfile("razer", "blackshark-v2-pro-2023", "Razer BlackShark V2 Pro (2023)", []),
        new UsbHeadsetProfile("razer", "blackshark-v2-hyperspeed", "Razer BlackShark V2 HyperSpeed", []),
        new UsbHeadsetProfile("razer", "blackshark-v3", "Razer BlackShark V3", []),
        new UsbHeadsetProfile("razer", "blackshark-v3-pro", "Razer BlackShark V3 Pro", []),
        new UsbHeadsetProfile("razer", "barracuda", "Razer Barracuda", []),
        new UsbHeadsetProfile("razer", "barracuda-x", "Razer Barracuda X", []),
        new UsbHeadsetProfile("razer", "barracuda-pro", "Razer Barracuda Pro", []),
        new UsbHeadsetProfile("razer", "kraken-v3-pro", "Razer Kraken V3 Pro", []),
        new UsbHeadsetProfile("razer", "kraken-v4", "Razer Kraken V4", []),
        new UsbHeadsetProfile("razer", "kraken-v4-pro", "Razer Kraken V4 Pro", []),
        new UsbHeadsetProfile("hyperx", "cloud-alpha-2", "HyperX Cloud Alpha 2", ["HyperX Cloud Alpha 2 Wireless"]),
        new UsbHeadsetProfile("hyperx", "cloud-iii-s", "HyperX Cloud III S Wireless", ["HyperX Cloud III S"]),
        new UsbHeadsetProfile("steelseries", "arctis-nova-7", "SteelSeries Arctis Nova 7", ["Arctis Nova 7"]),
        new UsbHeadsetProfile("jbl", "quantum-910", "JBL Quantum 910 Wireless", ["JBL Quantum 910"]),
        new UsbHeadsetProfile("logitech", "g-pro-x-2", "Logitech G PRO X 2 LIGHTSPEED", ["PRO X 2 LIGHTSPEED", "G PRO X 2 LIGHTSPEED"]),
    ];
}

public sealed class UsbHeadsetProfile(string brand, string model, string name, string[] aliases, bool batteryReady = false,
    IReadOnlyCollection<RazerPaSetting>? settings = null) : IDeviceProfile
{
    public string ModelId => $"{brand}-usb-{model}";
    public string DisplayName => name;
    public string ImageKey => ModelId;
    public DeviceForm Form => DeviceForm.Headphones;
    public UsbHeadsetProfile WithBattery(bool ready) => new(brand, model, name, aliases, ready, settings);

    /// <summary>
    /// Barracuda X (1532:0550) settings whose last read-back was valid while the headset was linked. Nothing
    /// else is offered: other receivers and other models stay battery-only.
    /// </summary>
    public IReadOnlyCollection<RazerPaSetting> Settings { get; } = settings ?? [];
    public UsbHeadsetProfile WithSettings(IEnumerable<RazerPaSetting> confirmed) =>
        new(brand, model, name, aliases, batteryReady, confirmed.Distinct().Order().ToArray());
    public bool Offers(RazerPaSetting setting) => Settings.Contains(setting);
    public IReadOnlyList<DeviceToggle> Toggles => Offers(RazerPaSetting.DoNotDisturb) ? [DeviceToggle.DoNotDisturb] : [];
    public IReadOnlyList<int> AutoPowerOffMinutes => Offers(RazerPaSetting.AutoOff) ? BarracudaXSettingValues.AutoOffMinutes : [];

    public IDeviceCapabilities Capabilities
    {
        get
        {
            var builder = DeviceCapabilities.CreateBuilder()
                .Set(DeviceCapability.BatteryLevel, batteryReady ? CapabilitySupport.Supported : CapabilitySupport.Unknown,
                    "Read-only standard HID percentage, or a documented query gated by exact receiver VID/PID and HID collection. No lighting or firmware writes.");
            if (Offers(RazerPaSetting.EqPreset))
                builder.Set(DeviceCapability.Equalizer, CapabilitySupport.Supported, "Barracuda X preset register 13, written only with read-back confirmation.");
            if (Offers(RazerPaSetting.EqPreset) && Offers(RazerPaSetting.CustomEq))
                builder.Set(DeviceCapability.CustomEqualizer, CapabilitySupport.Supported, "Barracuda X ten-band register 15, written only with read-back confirmation.");
            return builder.Build();
        }
    }
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets =>
        Offers(RazerPaSetting.EqPreset) ? BarracudaXSettingValues.EqualizerPresets(Offers(RazerPaSetting.CustomEq)) : [];
    public IReadOnlyList<Guid> ControlServiceUuids => [];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Usb) return 0;
        return new[] { name }.Concat(aliases).Where(n => Regex.IsMatch(identity.Name,
            @"(?<![\p{L}\p{N}])" + Regex.Escape(n).Replace(@"\ ", @"[\s_-]*") + @"(?![\p{L}\p{N}])(?![\s_-]+(?:Pro|Max|Plus|X|Chroma|Turbo|Lite|Ultra|HyperSpeed)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(n => 110 + n.Length).DefaultIfEmpty(0).Max();
    }
}
