// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

/// <summary>X3 model limits and control round trips; simulated device responses, not a hardware certification.</summary>
public class OppoX3Tests
{
    [Fact]
    public void X3LePairingDoesNotEnableItForOtherModelVariants()
    {
        var model = OppoModels.FindByProductId(0x067410)!;
        Assert.True(model.SupportsLeAudioPairing);
        Assert.Equal(100, new OppoDeviceProfile(model).MatchScore(new("x3", "OPPO Enco X3", new BluetoothAddress(0x112233445566))));
        Assert.False(OppoModels.FindByProductId(0x066810)!.SupportsLeAudioPairing);
        Assert.False(OppoModels.FindByProductId(0x06BC10)!.SupportsLeAudioPairing);
        Assert.False(model.Capabilities.IsAvailable(DeviceCapability.AdaptiveNoiseCancellation, false));
        Assert.False(model.Capabilities.IsAvailable(DeviceCapability.BassEngine, false));
        Assert.NotNull(model.CustomEqualizer);
        Assert.Equal(5, model.EqualizerPresets.Count);
    }

    [Fact]
    public async Task X3SessionSupportsNoiseAndMultipointWithoutQueryingUnprovidedFeatures()
    {
        var model = OppoModels.FindByProductId(0x067410)!;
        var device = new FakeOppoDevice { ProductId = 0x067410 };
        var identity = new DeviceIdentity("x3", "OPPO Enco X3", new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity });
        await using var session = new OppoSession(device.Channel);
        session.Start();
        await using var controller = new OppoDeviceController(session, new(model), identity, store, () => false);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await controller.InitializeAsync(ct));
        Assert.Equal(0x067410u, store.Current.ProductId);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, ct)).IsSuccess);
        Assert.False(store.Current.Toggles[DeviceToggle.DualDeviceConnection].Value);
        var option = model.NoiseModes.Single(m => m.SetIndex == 9).Option;
        Assert.True((await controller.SetNoiseControlAsync(new(option.Mode, option.LevelId), ct)).IsSuccess);
        Assert.DoesNotContain(device.Received, f => f.Command is OppoCommand.QueryPromptVolume or OppoCommand.QueryBassEngine);
    }
}
