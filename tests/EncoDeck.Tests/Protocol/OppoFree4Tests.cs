// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

/// <summary>Free4 interoperability facts from the official client; round trips use a simulated device.</summary>
public class OppoFree4Tests
{
    [Theory]
    [InlineData(0x068C10u)]
    [InlineData(0x06C010u)]
    public void ManagementFollowsFirmwareCapabilitiesWithoutDisablingMultipoint(uint productId)
    {
        var model = OppoModels.FindByProductId(productId)!;
        var bitmap = new byte[16];
        bitmap[3] = 0x20; // bit 29: device list, independent of bit 59: remote operations
        var listOnly = model.Adapt(new OppoCapabilityBitmap(bitmap), 118);
        Assert.False(listOnly.SupportsMultiConnectOperations);
        Assert.Contains(listOnly.FeatureSwitches, f => f.Toggle == DeviceToggle.DualDeviceConnection);
        bitmap[7] = 0x08;
        var managed = model.Adapt(new OppoCapabilityBitmap(bitmap), 118);
        Assert.True(managed.SupportsMultiConnectOperations);
        Assert.NotEqual(listOnly.ProfileKey, managed.ProfileKey);
        Assert.True(listOnly.Adapt(new OppoCapabilityBitmap(bitmap), 118).SupportsMultiConnectOperations);
    }

    [Theory]
    [InlineData(0x068C10u)]
    [InlineData(0x06C010u)]
    public void ModelLimitsFollowFree4Configuration(uint productId)
    {
        var model = OppoModels.FindByProductId(productId)!;
        Assert.Equal(OppoServiceUuids.Secondary, model.ServiceUuids[0]);
        Assert.Equal([62, 250, 1000, 4000, 8000, 16000], model.CustomEqualizer!.BandFrequencies);
        Assert.Equal((-6, 6, 3), (model.CustomEqualizer.MinGain, model.CustomEqualizer.MaxGain, model.CustomEqualizer.MaxPresets));
        Assert.Equal([0, 1], model.SpatialTypes);
        Assert.True(model.SpatialHiResMutex);
        Assert.False(model.Capabilities.IsAvailable(DeviceCapability.HeadTracking, false));
        Assert.False(model.Capabilities.IsAvailable(DeviceCapability.BassEngine, false));
        Assert.True(model.NoiseCycleShared);
        Assert.Equal(1, model.NoiseCycleMinimum);
        Assert.Equal([1, 11, 2, 0], model.NoiseCycleIndices);
        Assert.Equal(NoiseControlMode.Off, model.FindNoiseModeByReportedBitmap(1u << 3)!.Option.Mode);
        Assert.Equal(NoiseControlMode.Transparency, model.FindNoiseModeByReportedBitmap(1u << 8)!.Option.Mode);
        Assert.Contains(model.GestureRows, r => r.EditsNoiseCycle && !r.IsWritten);
        Assert.Contains(model.FeatureSwitches, f => f.Toggle == DeviceToggle.AutoVolume && f.FeatureId == 28);
        Assert.Contains((byte)28, model.FeatureStatusIds);
        Assert.DoesNotContain((byte)7, model.Adapt(null, 117).EqualizerPresets.Keys);
        Assert.Contains((byte)7, model.Adapt(null, 118).EqualizerPresets.Keys);
        if (productId == 0x06C010)
        {
            Assert.DoesNotContain((byte)8, model.Adapt(null, 135).EqualizerPresets.Keys);
            Assert.Contains((byte)8, model.Adapt(null, 136).EqualizerPresets.Keys);
        }
    }

    [Theory]
    [InlineData(0x068C10u)]
    [InlineData(0x06C010u)]
    public async Task Free4ControlsUseTheirOwnWireValues(uint productId)
    {
        await using var h = new Harness(productId);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await h.Controller.InitializeAsync(ct));
        Assert.False(h.Store.Current.Toggles[DeviceToggle.AutoVolume].Value);
        Assert.True((await h.Controller.SetToggleAsync(DeviceToggle.AutoVolume, true, ct)).IsSuccess);
        Assert.True(h.Store.Current.Toggles[DeviceToggle.AutoVolume].Value);
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.SetFeature && f.Payload.SequenceEqual(new byte[] { 28, 1 }));

        // Free4 has a single game switch and V1 spatial audio when V2 is not advertised.
        Assert.True((await h.Controller.SetToggleAsync(DeviceToggle.GamingMode, true, ct)).IsSuccess);
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.SetFeature && f.Payload.SequenceEqual(new byte[] { 6, 1 }));
        Assert.True((await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.Fixed, ct)).IsSuccess);
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.SetFeature && f.Payload.SequenceEqual(new byte[] { 27, 1 }));
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetSpatialAudioAsync(SpatialAudioMode.HeadTracking, ct)).Status);

        foreach (var option in h.Model.NoiseModes)
        {
            var result = await h.Controller.SetNoiseControlAsync(new(option.Option.Mode, option.Option.LevelId), ct);
            Assert.True(result.IsSuccess, $"{option.Option.Mode}/{option.Option.LevelId}, index {option.SetIndex}: {result}");
            Assert.Equal(option.SetIndex, h.Device.NoiseIndex);
        }
        Assert.True((await h.Controller.SetNoiseCycleAsync(EarSide.Both, 1u << 11, ct)).IsSuccess);
        Assert.Contains(h.Device.Received, f => f.Command == OppoCommand.SetNoiseControl && f.Payload.SequenceEqual(new byte[] { 2, 1, 0, 8 }));
    }

    [Fact]
    public async Task SmartVolumeCannotBeWrittenWithoutReportedStatus()
    {
        await using var h = new Harness(0x068C10);
        h.Device.Features.Remove(28);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await h.Controller.InitializeAsync(ct));
        Assert.False(h.Store.Current.Toggles.ContainsKey(DeviceToggle.AutoVolume));
        int before = h.Device.Received.Count;
        Assert.Equal(CommandStatus.Unsupported, (await h.Controller.SetToggleAsync(DeviceToggle.AutoVolume, true, ct)).Status);
        Assert.DoesNotContain(h.Device.Received.Skip(before), f => f.Command == OppoCommand.SetFeature);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public OppoModelDefinition Model { get; }
        public FakeOppoDevice Device { get; }
        public DeviceIdentity Identity { get; }
        public DeviceStateStore Store { get; } = new();
        public OppoSession Session { get; }
        public OppoDeviceController Controller { get; }

        public Harness(uint productId)
        {
            Model = OppoModels.FindByProductId(productId)!;
            Device = new FakeOppoDevice { ProductId = productId, ReportedIndexForSet = index => index };
            // Synthetic capabilities: exercise the official V1/single-game-switch fallback.
            Device.Capabilities[5] &= 0x7F;
            Device.Capabilities[6] &= 0xFD;
            Device.Features[28] = 0;
            Identity = new("free4", Model.DisplayName, new BluetoothAddress(0x112233445566));
            Store.Reset(DeviceState.Empty with { Device = Identity });
            Session = new(Device.Channel);
            Session.Start();
            Controller = new(Session, new(Model), Identity, Store, () => false);
        }

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            await Session.DisposeAsync();
        }
    }
}
