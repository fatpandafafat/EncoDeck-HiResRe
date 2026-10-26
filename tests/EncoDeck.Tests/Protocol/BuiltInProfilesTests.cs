// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands;

namespace EncoDeck.Tests.Protocol;

public class BuiltInProfilesTests
{
    private static readonly DeviceProfileRegistry Registry = new(BuiltInProfiles.Create());

    private static IControllableProfile? Match(string name) =>
        Registry.Match(new DeviceIdentity("dev", name, new BluetoothAddress(0x0011223344AA))) as IControllableProfile;

    [Fact]
    public void Model_ids_are_unique_across_brands()
    {
        var ids = Registry.Profiles.Select(p => p.ModelId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Registry.Profiles, p =>
        {
            if (p is not IControllableProfile) Assert.Empty(p.ControlServiceUuids);
        });
    }

    [Theory]
    [InlineData("OPPO Enco X4", "oppo", false)]
    [InlineData("realme Buds T500 Pro", "oppo", false)]
    [InlineData("vivo TWS 4", "vivo", false)]
    [InlineData("iQOO TWS Air3 Pro", "vivo", false)]
    [InlineData("HUAWEI FreeBuds Pro 3", "huawei", false)]
    [InlineData("Redmi Buds 6 Pro", "xiaomi", true)]
    [InlineData("EDIFIER W820NB", "edifier", false)]
    [InlineData("WH-1000XM5", "sony", false)]
    [InlineData("AKG N400NC", "akg", false)]
    [InlineData("Galaxy Buds2 Pro", "samsung", false)]
    [InlineData("Pixel Buds Pro 2", "google", false)]
    [InlineData("Bose QC45", "bose", false)]
    [InlineData("Nothing Ear (a)", "nothing", false)]
    [InlineData("CMF Buds 2", "nothing", false)]
    public void Names_resolve_to_their_brand(string name, string brand, bool generic)
    {
        var profile = Match(name);
        Assert.NotNull(profile);
        Assert.Equal(brand, profile.Brand);
        Assert.Equal(generic, profile.IsGeneric);
    }

    [Theory]
    [InlineData("Galaxy Buds FE")]
    [InlineData("Bose QC Ultra Earbuds (2nd Gen)")]
    [InlineData("AirPods Pro")]
    public void Other_brands_are_not_claimed(string name) => Assert.Null(Match(name));
}
