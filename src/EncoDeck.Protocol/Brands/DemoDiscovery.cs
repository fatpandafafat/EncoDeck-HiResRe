// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Brands.Xiaomi;

namespace EncoDeck.Protocol.Brands;

/// <summary>
/// Profiles as a successful live session would leave them, for Bluetooth-free UI demos. Several
/// brands only declare features after the earbuds answer; this applies the catalog's answers.
/// It is never hardware evidence and the Bluetooth runtime never calls it.
/// </summary>
public static class DemoDiscovery
{
    // Ids and names as a Sony preset capability answer reports them.
    private static readonly EqualizerPreset[] SonyPresets =
    [
        new("sony-0", "Off", "", CapabilitySupport.Supported),
        new("sony-16", "Bright", "", CapabilitySupport.Supported),
        new("sony-17", "Excited", "", CapabilitySupport.Supported),
        new("sony-18", "Mellow", "", CapabilitySupport.Supported),
    ];

    public static IDeviceProfile AfterSession(IDeviceProfile profile, string deviceName) => profile switch
    {
        SennheiserProfile { IsTw4: true } tw4 => tw4.WithLive(new(Noise: true, StatusNoise: true, Wind: true, SmartPause: true, LowLatency: true, Gestures: true)),
        SonyAkgProfile sony => sony.WithNoiseSupport(true).WithWearDetection(sony.Form == DeviceForm.Earbuds)
            .WithEqualizers(sony.Brand == "sony" ? SonyPresets : []),
        XiaomiDeviceProfile xiaomi => new[]
            {
                XiaomiConfig.DualConnection, XiaomiConfig.EqualizerPreset, XiaomiConfig.Noise, XiaomiConfig.Spatial, XiaomiConfig.LowLatency,
                XiaomiConfig.PromptVolume, XiaomiConfig.Gestures, XiaomiConfig.AutoAnswer, XiaomiConfig.WearState, XiaomiConfig.WearDetectionSwitch,
            }.Aggregate(xiaomi.ForDevice(deviceName), (p, id) => p.WithConfig(id)),
        HuaweiDeviceProfile huawei => huawei.WithSpatial(),
        MoondropProfile moondrop => moondrop.WithFeatures(moondrop.CatalogFeatures()),
        _ => profile,
    };
}
