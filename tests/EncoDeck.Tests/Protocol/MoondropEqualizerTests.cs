// SPDX-License-Identifier: GPL-3.0-only
// PEQ fixtures follow the MOONDROP EDGE firmware 1.4.0 and NEKOCAKE 1.0.0 measurements published in
// FEAKEuser/moondrop-control docs/PROTOCOL.md (GPL-3.0-or-later): preset list 06 00 01 02 03 04 3f, the
// 39-byte user curve read with 5/5 [00 04] (23 / 240 / 1400 / 2300 / 6900 Hz) and NEKOCAKE's 9-byte
// non-PEQ user config. The PUDDING battery frame is from cvbyisme/puddingpods-battery-fix (GPL-3.0).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed partial class MoondropTests
{
    /// <summary>EDGE user curve: [start 0][end 4][pre-gain 0] + per band [Hz][Q×4096][type 0][gain×60].</summary>
    private static readonly byte[] EdgeCurve =
    [
        0x00, 0x04, 0x00, 0x00,
        0x00, 0x17, 0x06, 0x66, 0x00, 0xFF, 0xA6, //   23 Hz, Q 0.40, −1.5 dB
        0x00, 0xF0, 0x51, 0x9A, 0x00, 0x00, 0xA2, //  240 Hz, Q 5.10, +2.7 dB
        0x05, 0x78, 0x64, 0xCD, 0x00, 0x00, 0xB4, // 1400 Hz, Q 6.30, +3.0 dB
        0x08, 0xFC, 0x1C, 0xCD, 0x00, 0xFF, 0x52, // 2300 Hz, Q 1.80, −2.9 dB
        0x1A, 0xF4, 0x13, 0x33, 0x00, 0x00, 0xB4, // 6900 Hz, Q 1.20, +3.0 dB
    ];

    [Fact]
    public void PeqCodecRoundTripsTheDeviceCurveAndRejectsUnknownLayouts()
    {
        var peq = MoondropPeq.Decode(EdgeCurve)!;
        Assert.Equal(new[] { 23, 240, 1400, 2300, 6900 }, peq.Bands.Select(b => b.FrequencyHz));
        Assert.Equal(new[] { -15, 27, 30, -29, 30 }, peq.Bands.Select(b => MoondropPeq.Tenths(b.Gain)));
        Assert.Equal(EdgeCurve, peq.Encode());
        byte[] filter = (byte[])EdgeCurve.Clone();
        filter[8] = 1;
        Assert.Null(MoondropPeq.Decode(filter));
        byte[] range = (byte[])EdgeCurve.Clone();
        range[1] = 5;
        Assert.Null(MoondropPeq.Decode(range));
        Assert.Null(MoondropPeq.Decode(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0 }));
        // Wire commands 0A 05 (read) / 0A 06 (write), framed for RFCOMM.
        Assert.Equal(new byte[] { 0xFF, 4, 0, 2, 0, 0x1D, 0x0A, 5, 0, 4 }, GaiaFrameCodec.Encode(5, 5, [0, 4], true));
        Assert.Equal(new byte[] { 0xFF, 4, 0, 39, 0, 0x1D, 0x0A, 6 }, GaiaFrameCodec.Encode(5, 6, EdgeCurve, true)[..8]);
    }

    [Fact]
    public void PuddingLengthFieldCountsOnlyThePayload()
    {
        var decoder = new GaiaFrameDecoder();
        var frame = Assert.Single(decoder.Feed([0xFF, 4, 0, 6, 0, 0x1D, 0x1B, 1, 1, 0x64, 2, 0x64, 3, 0x5E], true));
        Assert.Equal((13, 2, 1), (frame.Feature, frame.Type, frame.Command));
        Assert.Equal(new byte[] { 1, 100, 2, 100, 3, 94 }, frame.Payload);
    }

    [Fact]
    public async Task EdgeUserCurveWritesOnlyGainsAndRequiresByteExactReadback()
    {
        var (profile, store, identity) = EqSession("MOONDROP EDGE");
        var device = new PodChannel(false) { AncFeature = 8, ExtendedNoiseResponse = true, Peq = true, PeqCurve = EdgeCurve };
        await using var controller = profile.CreateController(device, new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));

        var live = (MoondropProfile)store.Current.Profile!;
        Assert.Equal(new BandEqualizerSpec(-120, 30), live.BandEqualizer);
        Assert.True(live.Capabilities.IsAvailable(DeviceCapability.CustomEqualizer, false));
        Assert.True(live.Capabilities.IsAvailable(DeviceCapability.Equalizer, false));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => MoondropProfile.FactoryPresetId((byte)i)), live.EqualizerPresets.Select(p => p.Id));
        // One localized format string covers every preset number, including ids beyond the first five.
        Assert.Equal(Enumerable.Range(1, 5).Select(n => $"Eq_DevicePreset:{n}"), live.EqualizerPresets.Select(p => p.ResourceKey));
        Assert.Equal(new EqualizerState(MoondropProfile.FactoryPresetId(4), 4), store.Current.Equalizer?.Value);
        var curve = Assert.Single(store.Current.CustomEqualizers!.Value);
        Assert.Equal(MoondropDeviceController.UserCurvePreset, curve.Id);
        Assert.False(curve.IsSelected);
        Assert.Equal(new[] { -15, 27, 30, -29, 30 }, curve.Bands.Select(b => b.Gain));

        var target = curve with { Bands = [.. curve.Bands.Select((b, i) => i == 0 ? b with { Gain = -50 } : b)] };
        Assert.True((await controller.SaveCustomEqualizerAsync(target, Ct)).IsSuccess);
        byte[] expected = (byte[])EdgeCurve.Clone();
        expected[9] = 0xFE; expected[10] = 0xD4; // −300 = −5.0 dB × 60; every other byte is written back unchanged.
        Assert.Equal(expected, Assert.Single(device.PeqWrites));
        Assert.Equal(-50, store.Current.CustomEqualizers!.Value[0].Bands[0].Gain);

        var outside = target with { Bands = [.. target.Bands.Select((b, i) => i == 1 ? b with { Gain = 40 } : b)] };
        Assert.Equal(CommandStatus.Rejected, (await controller.SaveCustomEqualizerAsync(outside, Ct)).Status);
        Assert.Single(device.PeqWrites);

        // A dropped write is retried once and still confirmed by readback.
        device.DropPeqWrites = 1;
        var flat = target with { Bands = [.. target.Bands.Select(b => b with { Gain = 0 })] };
        Assert.True((await controller.SaveCustomEqualizerAsync(flat, Ct)).IsSuccess);
        Assert.Equal(3, device.PeqWrites.Count);
        device.CorruptPeq = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SaveCustomEqualizerAsync(target, Ct)).Status);
        Assert.Empty(store.Current.PendingCommands);

        Assert.True((await controller.SelectCustomEqualizerAsync(MoondropDeviceController.UserCurvePreset, Ct)).IsSuccess);
        Assert.Equal(MoondropDeviceController.UserCurvePreset, device.ActivePreset);
        Assert.True(store.Current.CustomEqualizers!.Value[0].IsSelected);
        Assert.True((await controller.SetEqualizerPresetAsync(MoondropProfile.FactoryPresetId(1), Ct)).IsSuccess);
        Assert.Equal(1, device.ActivePreset);
        Assert.Equal(new EqualizerState(MoondropProfile.FactoryPresetId(1), 1), store.Current.Equalizer?.Value);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync(MoondropProfile.FactoryPresetId(9), Ct)).Status);
    }

    [Fact]
    public async Task NekocakePresetsStayExperimentalAndItsNonPeqUserConfigIsNeverEditable()
    {
        var (profile, store, identity) = EqSession("MOONDROP NEKOCAKE");
        bool experimental = false;
        var device = new PodChannel(false) { FeatureList = [0, 1, 3, 5, 6], PeqCurve = [0, 1, 0, 0, 0, 0, 0, 0, 0] };
        await using var controller = profile.CreateController(device, new ControllerContext(identity, store, () => experimental, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(Ct));

        var live = (MoondropProfile)store.Current.Profile!;
        Assert.Null(live.BandEqualizer);
        Assert.Null(store.Current.CustomEqualizers);
        Assert.False(live.Capabilities.IsAvailable(DeviceCapability.CustomEqualizer, true));
        Assert.False(live.Capabilities.IsAvailable(DeviceCapability.Equalizer, false));
        Assert.True(live.Capabilities.IsAvailable(DeviceCapability.Equalizer, true));
        Assert.False(live.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, true));

        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync(MoondropProfile.FactoryPresetId(1), Ct)).Status);
        Assert.Equal(4, device.ActivePreset);
        experimental = true;
        Assert.True((await controller.SetEqualizerPresetAsync(MoondropProfile.FactoryPresetId(1), Ct)).IsSuccess);
        Assert.Equal(1, device.ActivePreset);
        var any = new CustomEqualizer(MoondropDeviceController.UserCurvePreset, string.Empty, -120, 30, false, [new(100, 0)]);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SaveCustomEqualizerAsync(any, Ct)).Status);
        Assert.Empty(device.PeqWrites);
    }

    private static (MoondropProfile Profile, DeviceStateStore Store, DeviceIdentity Identity) EqSession(string name)
    {
        var identity = new DeviceIdentity("moondrop-eq-test", name, new BluetoothAddress(0));
        var profile = (MoondropProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile, store, identity);
    }
}
