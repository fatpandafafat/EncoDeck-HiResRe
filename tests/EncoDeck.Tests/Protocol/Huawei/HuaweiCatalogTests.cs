// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Huawei;

namespace EncoDeck.Tests.Protocol.Huawei;

public class HuaweiCatalogTests
{
    private static DeviceIdentity Named(string name) => new("dev", name, new BluetoothAddress(0x112233445566));

    [Fact]
    public void BuiltIn_HasEveryModelAndOneGeneric()
    {
        var profiles = HuaweiProfiles.CreateBuiltIn().Cast<IControllableProfile>().ToList();
        Assert.Equal(28, profiles.Count);
        Assert.Single(profiles, p => p.IsGeneric);
        Assert.All(profiles, p =>
        {
            Assert.Equal("huawei", p.Brand);
            Assert.StartsWith("huawei-", p.ModelId);
            Assert.Equal(p.ModelId, p.ImageKey);
            Assert.Equal([HuaweiCommands.SppServiceUuid], p.ControlServiceUuids);
        });
        Assert.Equal(profiles.Count, profiles.Select(p => p.ModelId).Distinct().Count());
    }

    [Fact]
    public void Profiles_DeclareOnlyWhatTheyOffer()
    {
        foreach (var p in HuaweiProfiles.CreateBuiltIn().Cast<HuaweiDeviceProfile>())
        {
            Assert.Equal(p.NoiseControlOptions.Count > 0, p.Capabilities.Get(DeviceCapability.NoiseCancellation).Support == CapabilitySupport.Supported);
            Assert.Equal(p.EqualizerPresets.Count > 0, p.Capabilities.Get(DeviceCapability.Equalizer).Support == CapabilitySupport.Supported);
            Assert.Equal(p.EqualizerPresets.Count, p.EqualizerPresets.Select(e => e.Id).Distinct().Count());
            Assert.All(p.NoiseControlOptions, o => Assert.False(string.IsNullOrEmpty(o.ResourceKey)));
        }
    }

    [Theory]
    [InlineData("HUAWEI FreeBuds Pro 3", "huawei-freebuds-pro-3", 100)]
    [InlineData("FreeBuds Pro 3", "huawei-freebuds-pro-3", 100)]
    [InlineData("HUAWEI FreeBuds Pro", "huawei-freebuds-pro", 100)]
    [InlineData("HUAWEI FreeBuds Pro 3 (A1B2)", "huawei-freebuds-pro-3", 50)]
    [InlineData("HUAWEI FreeBuds 5i", "huawei-freebuds-5i", 100)]
    [InlineData("HUAWEI FreeLace Pro 2", "huawei-freelace-pro-2", 100)]
    [InlineData("HUAWEI Eyewear 2", "huawei-eyewear-2", 100)]
    [InlineData("HUAWEI FreeBuds 6", "huawei-freebuds-6", 100)]
    [InlineData("HUAWEI FreeBuds 7", "huawei-freebuds-7", 100)]
    [InlineData("HUAWEI FreeBuds 7i", "huawei-freebuds-7i", 100)]
    [InlineData("HUAWEI FreeClip 2 典藏版", "huawei-freeclip-2", 100)]
    [InlineData("HUAWEI FreeBuds SE 4 ANC", "huawei-freebuds-se-4", 100)]
    [InlineData("HUAWEI Eyewear 3", "huawei-eyewear-3", 100)]
    [InlineData("HUAWEI Eyewear", "huawei-eyewear", 100)]
    [InlineData("HUAWEI FreeBuds X9", "huawei-generic", 1)]
    public void Registry_MatchesByName(string name, string expected, int score)
    {
        var registry = new DeviceProfileRegistry(HuaweiProfiles.CreateBuiltIn());
        var match = registry.Match(Named(name));
        Assert.NotNull(match);
        Assert.Equal(expected, match.ModelId);
        Assert.Equal(score, match.MatchScore(Named(name)));
    }

    [Fact]
    public void Registry_IgnoresOtherBrands()
    {
        var registry = new DeviceProfileRegistry(HuaweiProfiles.CreateBuiltIn());
        Assert.Null(registry.Match(Named("OPPO Enco X4")));
    }

    [Fact]
    public void Pro3_UsesProEqIdsAndFourLevels()
    {
        var model = HuaweiModels.All.Single(m => m.ModelId == "huawei-freebuds-pro-3");
        Assert.Equal(5, model.EqPresets.Single(p => p.PresetId == "default").ProtocolId);
        Assert.Equal(["dynamic", "cozy", "general", "ultra"], model.AncLevels.Select(l => l.LevelId));
        // HuaweiPods' Pro 3 / 7i capture: dynamic 1, cozy 0, general 2, ultra 3.
        Assert.Equal([1, 0, 2, 3], model.AncLevels.Select(l => (int)l.SubMode));
        Assert.Equal(HuaweiProtocol.Legacy, model.Protocol);
    }

    [Theory]
    [InlineData("huawei-freebuds-5i", 3, 1, 0, 2)]
    [InlineData("huawei-freebuds-pro-5", 3, 1, 0, 2)]
    [InlineData("huawei-freebuds-7i", 1, 0, 2, 3)]
    public void AncLevelCodes_FollowCommunityCaptures(string id, int dynamic, int cozy, int general, int ultra)
    {
        var levels = HuaweiModels.All.Single(m => m.ModelId == id).AncLevels.ToDictionary(l => l.LevelId, l => (int)l.SubMode);
        Assert.Equal([dynamic, cozy, general, ultra], new[] { levels["dynamic"], levels["cozy"], levels["general"], levels["ultra"] });
    }

    [Fact]
    public void NewAndUpdatedModels_DeclareCommunityEvidencedFeatures()
    {
        static HuaweiDeviceProfile P(string id) => new(HuaweiModels.All.Single(m => m.ModelId == id));

        // FreeBuds Pro 4: off / noise cancelling / transparency read back on Windows (OpenFreebuds #138), no level labels.
        var pro4 = P("huawei-freebuds-pro-4");
        Assert.Equal([NoiseControlMode.Off, NoiseControlMode.NoiseCancellation, NoiseControlMode.Transparency], pro4.NoiseControlOptions.Select(o => o.Mode));
        Assert.All(pro4.NoiseControlOptions, o => Assert.Null(o.LevelId));
        Assert.True(pro4.Model.WearState);

        // FreeBuds 6: three levels, no transparency, preset 0 is its default (HuaweiPods).
        var six = P("huawei-freebuds-6");
        Assert.DoesNotContain(six.NoiseControlOptions, o => o.Mode == NoiseControlMode.Transparency);
        Assert.Equal(0, six.Model.EqPresets.Single(p => p.PresetId == "default").ProtocolId);

        // Smart features stay closed until the earbuds report them.
        var seven = P("huawei-freebuds-7");
        Assert.Equal(HuaweiSmartFeatures.AdaptiveVolume | HuaweiSmartFeatures.HeadGestures, seven.Model.SmartFeatures);
        Assert.DoesNotContain(DeviceToggle.AutoVolume, seven.Toggles);
        Assert.False(seven.Model.DualConnect);

        var eyewear3 = P("huawei-eyewear-3");
        Assert.Equal(DeviceForm.Headphones, eyewear3.Form);
        Assert.Empty(eyewear3.NoiseControlOptions);
    }
}
