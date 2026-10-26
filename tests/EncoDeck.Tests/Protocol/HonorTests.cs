// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Honor;
using EncoDeck.Protocol.Brands.Huawei;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public class HonorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Device : IDuplexByteChannel
    {
        private readonly FakeByteChannel _inner = new();
        public string Description => "HONOR MBB fixture";
        public List<HuaweiFrame> Writes { get; } = [];
        public byte[] Noise = [0, 0];
        public byte Wear, Dual, Eq = 4;
        public bool IgnoreWrites, RejectWrites, MalformedBattery, HuaweiAck, Choice;
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _inner.WriteAsync(data, ct);
            foreach (var f in new MbbFrameCodec().Feed(data.Span))
            {
                Writes.Add(f);
                byte[]? answer = f.Command switch
                {
                    0x0108 => MalformedBattery ? [2, 3, 50] : [2, 3, 80, 70, 60, 3, 3, 0, 1, 0],
                    0x2B2A when Choice => [1, 1, Noise[1]],
                    0x2B2A => [1, 2, .. Noise],
                    0x2B11 => [1, 1, Wear],
                    0x2B2E => [1, 1, Dual],
                    0x2B6D => [1, 1, Eq, 2, 3, 0, 4, 5],
                    _ => null,
                };
                if (answer is null)
                {
                    // Result code 100000 is how HUAWEI-derived firmware acknowledges a write.
                    answer = RejectWrites ? [127, 1, 1] : HuaweiAck ? [127, 4, 0, 1, 0x86, 0xA0] : [1, 1, 0];
                    if (!IgnoreWrites && !RejectWrites)
                    {
                        switch (f.Command)
                        {
                            case 0x2B04 when f.Payload.Length == 3: Noise = [0, f.Payload[2]]; break;
                            case 0x2B04: Noise = [f.Payload[3] == 0xFF ? (byte)1 : f.Payload[3], f.Payload[2]]; break;
                            case 0x2B10: Wear = f.Payload[2]; break;
                            case 0x2B2F: Dual = f.Payload[2]; break;
                            case 0x2B6E: Eq = f.Payload[2]; break;
                        }
                    }
                }
                _inner.Receive(new MbbFrameCodec().Encode(f.Command, answer));
            }
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
    private static (IDeviceController Controller, DeviceStateStore Store) Create(Device device, string product = "L001")
    {
        var model = HonorProfiles.Models.Single(m => m.ProductId == product);
        var profile = new HonorDeviceProfile(model);
        var identity = new DeviceIdentity("honor-test", model.DisplayName, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System)), store);
    }

    [Theory]
    [InlineData("HONOR Earbuds 3 Pro", "honor-l001")]
    [InlineData("荣耀Earbuds 3i 尊享版", "honor-le7p")]
    [InlineData("HONOR Earbuds X3i (1234)", "honor-l010")]
    [InlineData("HONOR Earbuds X3 SE", "honor-l010")]
    [InlineData("HONOR Earbuds X5", "honor-l013")]
    public void ModelAliases_DoNotSelectAnotherModel(string name, string expected)
    {
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        var matched = registry.Match(new("test", name, default));
        Assert.Equal(expected, matched?.ModelId);
        var key = new DeviceResourceKey(expected, null, null, name, matched!.DisplayName);
        Assert.True(key.CanDownload);
        Assert.False(key.UsesOplusId);
        Assert.Equal(expected, OfficialArtworkCatalog.Lookup(key)?.ModelId);
    }

    [Fact]
    public void NewModelsAndX3_DoNotInheritUnverifiedMbbControls()
    {
        Assert.DoesNotContain(HonorProfiles.Models, m => m.ProductId == "ZA08");
        var registry = new DeviceProfileRegistry(HonorProfiles.CreateBuiltIn());
        foreach (string name in new[] { "HONOR Earbuds X3", "HONOR Earbuds X9", "HONOR Earbuds 4" })
        {
            var profile = Assert.IsType<HonorDeviceProfile>(registry.Match(new("test", name, default)));
            Assert.Empty(profile.ControlServiceUuids);
            Assert.Empty(profile.NoiseControlOptions);
        }
    }

    [Fact]
    public async Task OfficialMbbFraming_BatteryAncLevelsAndReversedDualCommands()
    {
        var device = new Device();
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(80, store.Current.Battery.Left!.Value.Level);
        Assert.True(store.Current.Battery.Right!.Value.IsCharging);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "dynamic"), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 3, 1 }, device.Noise);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, true, Ct)).IsSuccess);
        Assert.Equal(1, device.Dual);
        Assert.Contains(device.Writes, f => f.Command == 0x2B2F && f.Payload.SequenceEqual(new byte[] { 1, 1, 1 }));
        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, true, Ct)).IsSuccess);
        device.IgnoreWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        device.RejectWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, Ct)).Status);
        Assert.DoesNotContain(device.Writes, f => f.Command is 0x2BB4 or 0x2B49 or 0x2B4A);
    }

    [Fact]
    public async Task Eq_RequiresDevicePresetListAndUsesHonorCommandAndField()
    {
        var device = new Device();
        var (controller, store) = Create(device, "L013"); await using var session = controller;
        Assert.Empty(store.Current.Profile!.EqualizerPresets);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("5", Ct)).Status);
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(new[] { "0", "4", "5" }, store.Current.Profile!.EqualizerPresets.Select(p => p.Id));
        Assert.Equal(CapabilitySupport.Supported, store.Current.Profile.Capabilities.Get(DeviceCapability.Equalizer).Support);
        Assert.True((await controller.SetEqualizerPresetAsync("5", Ct)).IsSuccess);
        Assert.Equal(5, store.Current.Equalizer!.Value.RawPresetValue);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("10", Ct)).Status);
        device.IgnoreWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("4", Ct)).Status);
        Assert.Equal(5, store.Current.Equalizer!.Value.RawPresetValue);
        Assert.DoesNotContain(device.Writes, f => f.Command == 0x2B2A);
    }

    [Fact]
    public async Task TruncatedBattery_DoesNotEstablishControlSession()
    {
        var (controller, store) = Create(new Device { MalformedBattery = true }); await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        Assert.Null(store.Current.Battery.Left);
    }

    [Fact]
    public async Task ResultCodeAcknowledgement_AndModeButtonKeepTheLevel()
    {
        // Earbuds 2 SE runs HUAWEI-derived firmware (OpenFreebuds #56): writes answer 7F 04 000186A0.
        var device = new Device { HuaweiAck = true };
        var (controller, store) = Create(device); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, true, Ct)).IsSuccess);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Contains(device.Writes, f => f.Command == 0x2B04 && f.Payload.SequenceEqual(new byte[] { 1, 2, 1, 0xFF }));
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "general"), store.Current.NoiseControl!.Value);
    }

    [Theory]
    [InlineData("HONOR CHOICE Earbuds X5", "honor-choice-x5")]
    [InlineData("HONOR CHOICE Earbuds X5 Pro", "honor-choice-x5-pro")]
    public void ChoiceModels_AreMatchedByTheirOwnNames(string name, string expected)
    {
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        var profile = Assert.IsType<HonorDeviceProfile>(registry.Match(new("test", name, default)));
        Assert.Equal(expected, profile.ModelId);
        Assert.Equal([NoiseControlMode.Off, NoiseControlMode.NoiseCancellation, NoiseControlMode.Transparency], profile.NoiseControlOptions.Select(o => o.Mode));
        Assert.Empty(profile.Toggles);
        Assert.Empty(profile.EqualizerPresets);
    }

    [Fact]
    public async Task Choice_UsesEmptyQueriesAndAOneByteNoiseWrite()
    {
        // Nezerkill/honor-earbuds (HONOR CHOICE Earbuds X5, tested): 0108 and 2B2A without TLVs, 2B04 TLV 1 = [mode].
        var device = new Device { Choice = true };
        var (controller, store) = Create(device, "CHOICE-X5"); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(80, store.Current.Battery.Left!.Value.Level);
        Assert.Contains(device.Writes, f => f.Command == 0x0108 && f.Payload.Length == 0);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl!.Value.Mode);

        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Contains(device.Writes, f => f.Command == 0x2B04 && f.Payload.SequenceEqual(new byte[] { 1, 1, 2 }));
        Assert.Equal(new NoiseControlState(NoiseControlMode.Transparency), store.Current.NoiseControl!.Value);
        device.IgnoreWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
    }
}
