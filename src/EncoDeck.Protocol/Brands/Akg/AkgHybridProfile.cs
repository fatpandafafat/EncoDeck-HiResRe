// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

public sealed class AkgHybridProfile(bool earbuds, bool noiseReady = false) : IControllableProfile
{
    public string Brand => "akg";
    public string ModelId => earbuds ? "akg-n5hybrid" : "akg-n9hybrid";
    public string DisplayName => earbuds ? "AKG N5 Hybrid" : "AKG N9 Hybrid";
    public string ImageKey => ModelId;
    public DeviceForm Form => earbuds ? DeviceForm.Earbuds : DeviceForm.Headphones;
    public bool IsGeneric => false;
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, CapabilitySupport.Supported, "RACE battery 0x0CD6 / charging 0x0009 / case 0x000B, observed on a real N9 Hybrid (kalicyh/Raito); Windows validation pending.")
        .Set(DeviceCapability.ChargingStatus, CapabilitySupport.Supported, "RACE GET_CHARGE_INFO 0x0009 per role.")
        .Set(DeviceCapability.NoiseCancellation, noiseReady ? CapabilitySupport.Supported : CapabilitySupport.Unknown,
            "Experimental opt-in: 0x0E06 write with a valid 0x0901 readback; values are from a JBL RACE note, unverified on AKG.").Build();
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => noiseReady ?
    [new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
     new(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported),
     new(NoiseControlMode.Transparency, null, "Ambient sound", "NoiseMode_Transparency", CapabilitySupport.Supported)] : [];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => [];
    /// <summary>
    /// Classic RACE / SPP services first (the paired classic address is known; the N9 Hybrid also
    /// advertises a separate "-LE" peripheral), then RACE over BLE GATT as seen on a real N9 Hybrid.
    /// </summary>
    public IReadOnlyList<Guid> ControlServiceUuids => [AkgBleUuids.RaceSpp, AkgBleUuids.Spp, AkgBleUuids.RaceService];
    public int MatchScore(DeviceIdentity identity)
    {
        if (identity.Transport != DeviceTransport.Bluetooth) return 0;
        string actual = Normalize(identity.Name);
        string prefix = earbuds ? "akgn5" : "akgn9";
        return actual == prefix || actual == prefix + "hybrid" ? 110 : 0;
    }
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new AkgHybridController(channel, context, earbuds);
}
