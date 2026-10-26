// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Vivo;

namespace EncoDeck.Tests.Protocol.Vivo;

public class VivoCatalogTests
{
    private static readonly DeviceProfileRegistry Registry = new(VivoProfiles.CreateBuiltIn());

    private static IDeviceProfile? Match(string name) => Registry.Match(new DeviceIdentity("id", name, new BluetoothAddress(0x112233445566)));

    [Fact]
    public void Catalog_HasUniqueIdsAndEveryModelResolvesByName()
    {
        var profiles = VivoProfiles.CreateBuiltIn();
        Assert.Equal(VivoCatalog.All.Count + 1, profiles.Count);
        Assert.Equal(43, VivoCatalog.All.Count);
        Assert.Equal(profiles.Count, profiles.Select(p => p.ModelId).Distinct().Count());
        Assert.Contains(VivoCatalog.All, m => m.Name.StartsWith("iQOO", StringComparison.Ordinal));
        foreach (var model in VivoCatalog.All)
        {
            Assert.Equal(model.ModelId, Match(model.Name)?.ModelId);
            Assert.All(profiles, p => Assert.Equal("vivo", ((IControllableProfile)p).Brand));
        }
    }

    [Fact]
    public void Matching_PrefersExactThenProjectThenContainsThenGeneric()
    {
        Assert.Equal("vivo-tws-4-hi-fi", Match("vivo TWS 4 Hi-Fi")?.ModelId);
        Assert.Equal("vivo-tws-3e", Match("DPD2321A LE")?.ModelId);
        Assert.Equal("vivo-tws-4", Match("My vivo TWS 4")?.ModelId);
        var generic = Assert.IsType<VivoDeviceProfile>(Match("vivo TWS 9 Ultra"));
        Assert.True(generic.IsGeneric);
        Assert.Null(Match("OPPO Enco X4"));
        Assert.Null(Match("vivo X200 Pro"));
    }

    [Fact]
    public void Profiles_FollowTheModelGenerationAndFeatures()
    {
        var tws3e = VivoCatalog.All.Single(m => m.ModelId == "vivo-tws-3e");
        Assert.Equal(3, tws3e.GaiaVersion);
        Assert.Equal(new byte[] { 3 }, tws3e.NoiseSetSuffix);
        var air3Pro = VivoCatalog.All.Single(m => m.ModelId == "iqoo-tws-air3-pro");
        Assert.Equal(new byte[] { 4, 0 }, air3Pro.NoiseSetSuffix);
        Assert.Empty(air3Pro.NoiseQueryPayload);

        var tws4 = new VivoDeviceProfile(VivoCatalog.All.Single(m => m.ModelId == "vivo-tws-4"));
        Assert.Equal(4, tws4.Model.GaiaVersion);
        Assert.Equal(3, tws4.NoiseControlOptions.Count);
        Assert.Equal(["vivo-eq-0", "vivo-eq-1", "vivo-eq-2", "vivo-eq-3", "vivo-eq-5"], tws4.EqualizerPresets.Select(p => p.Id));
        Assert.Equal(CapabilitySupport.Supported, tws4.Capabilities.Get(DeviceCapability.GamingMode).Support);
        Assert.Equal(CapabilitySupport.Unsupported, tws4.Capabilities.Get(DeviceCapability.SpatialAudio).Support);
        Assert.Equal([VivoConstants.ServiceUuid, VivoConstants.GattServiceUuid], tws4.ControlServiceUuids);

        var tws1 = new VivoDeviceProfile(VivoCatalog.All.Single(m => m.ModelId == "vivo-tws-1"));
        Assert.Empty(tws1.NoiseControlOptions);
        Assert.Equal(CapabilitySupport.Unsupported, tws1.Capabilities.Get(DeviceCapability.FindMyEarbuds).Support);
    }
}
