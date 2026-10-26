// SPDX-License-Identifier: GPL-3.0-only
// Test cases ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Mode layouts are synthetic fixtures, not hardware captures.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Bose;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public class BoseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] ModeFixture(BoseModel model, byte index, byte cnc = 0)
    {
        var p = new byte[model.ModeStatusLength()];
        p[0] = index; p[2] = index < 2 ? (byte)(index + 1) : (byte)12;
        p[3] = index < (model == BoseModel.QuietComfort45 ? 2 : 4) ? (byte)0 : (byte)1; p[4] = p[3]; p[5] = 1;
        byte[] name = System.Text.Encoding.UTF8.GetBytes(index == 0 ? "Quiet" : index == 1 ? "Aware" : "Custom");
        name.CopyTo(p, 6); p[42] = cnc;
        if (model != BoseModel.QuietComfort45) p[47] = 1;
        return p;
    }

    /// <summary>Synthetic BMAP headphones answering the GETs and the mode START.</summary>
    private sealed class Device : IDuplexByteChannel
    {
        private readonly FakeByteChannel _inner = new();
        private readonly BoseModel _model;
        public string Description => "Bose BMAP fixture";
        public List<BoseFrame> Commands { get; } = [];
        public Dictionary<byte, byte[]> Modes { get; } = [];
        public byte Current;
        public byte[] Battery = [80, 255, 255, 0];
        public byte[]? ModeCount;
        public bool IgnoreModeWrite, MalformedReadback, RejectStart;
        private bool _written;

        public Device(BoseModel model)
        {
            _model = model;
            for (int i = 0; i < model.MaximumModeCount(); i++) Modes[(byte)i] = ModeFixture(model, (byte)i, i == 1 ? (byte)10 : (byte)0);
        }

        private void Emit(BoseFrame frame) => _inner.Receive(BoseFrameCodec.Encode(frame));

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _inner.WriteAsync(data, ct);
            foreach (var f in new BoseFrameDecoder().Feed(data.Span))
            {
                Commands.Add(f);
                BoseFrame reply;
                switch (f.Block, f.Function, f.Operation)
                {
                    case (0, 1, BoseOperation.Get): reply = new(0, 1, BoseOperation.Status, [1, 0, 0]); break;
                    case (0, 5, BoseOperation.Get): reply = new(0, 5, BoseOperation.Status, "8.2.20+test"u8.ToArray()); break;
                    case (2, 2, BoseOperation.Get): reply = new(2, 2, BoseOperation.Status, Battery); break;
                    case (31, 8, BoseOperation.Get): reply = new(31, 8, BoseOperation.Status, ModeCount ?? [(byte)_model.MaximumModeCount(), 0, 3]); break;
                    case (31, 6, BoseOperation.Get): reply = new(31, 6, BoseOperation.Status, Modes.TryGetValue(f.Payload[0], out var m) ? m : []); break;
                    case (31, 3, BoseOperation.Get): reply = new(31, 3, BoseOperation.Status, _written && MalformedReadback ? [] : [Current]); break;
                    case (31, 3, BoseOperation.Start) when RejectStart: reply = new(31, 3, BoseOperation.Error, [5]); break;
                    case (31, 3, BoseOperation.Start):
                        _written = true;
                        if (!IgnoreModeWrite) Current = f.Payload[0];
                        // The device can push STATUS before RESULT; neither confirms the write.
                        Emit(new(31, 3, BoseOperation.Status, [Current]));
                        reply = new(31, 3, BoseOperation.Result, [Current]);
                        break;
                    default: reply = new(f.Block, f.Function, BoseOperation.Error, [4]); break;
                }
                Emit(reply);
            }
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(Device device, BoseModel model)
    {
        var profile = (BoseProfile)BoseProfiles.CreateBuiltIn().Single(p => ((BoseProfile)p).Model == model);
        var identity = new DeviceIdentity("bose-test", profile.DisplayName, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System)), store);
    }

    [Fact]
    public void Stream_SplitsEveryBoundaryAndKeepsRouteBits()
    {
        BoseFrame[] frames = [new(2, 2, BoseOperation.Status, [80, 255, 255, 0]), new(31, 3, BoseOperation.Result, [1]), new(31, 3, BoseOperation.Status, [7], 0x50)];
        byte[] bytes = [.. frames.SelectMany(f => BoseFrameCodec.Encode(f))];
        Assert.Equal(new byte[] { 2, 2, 3, 4, 80, 255, 255, 0 }, bytes[..8]);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var decoder = new BoseFrameDecoder();
            var actual = decoder.Feed(bytes.AsSpan(0, split)).Concat(decoder.Feed(bytes.AsSpan(split))).ToList();
            Assert.Equal(frames.Length, actual.Count);
            for (int i = 0; i < frames.Length; i++)
            {
                Assert.Equal((frames[i].Block, frames[i].Function, frames[i].Operation, frames[i].Route), (actual[i].Block, actual[i].Function, actual[i].Operation, actual[i].Route));
                Assert.Equal(frames[i].Payload, actual[i].Payload);
            }
        }
        Assert.False(frames[2].Matches(31, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoseFrameCodec.Encode(new(2, 2, BoseOperation.Get, new byte[256])));
    }

    [Fact]
    public void CorruptStream_IsNotResynchronizedUntilReset()
    {
        var decoder = new BoseFrameDecoder();
        Assert.Empty(decoder.Feed([2, 2, 15, 0]));
        Assert.True(decoder.Failed);
        byte[] valid = BoseFrameCodec.Encode(new(31, 3, BoseOperation.Status, [0]));
        Assert.Empty(decoder.Feed(valid));
        decoder.Reset();
        Assert.Single(decoder.Feed(valid));
        Assert.Empty(decoder.Feed([31, 3, 3, 1]));
        decoder.Reset();
        Assert.Single(decoder.Feed(valid));
        Assert.Empty(decoder.Feed(new byte[16_385]));
        Assert.True(decoder.Failed);
    }

    [Fact]
    public void Battery_KeepsComponentIdsAndUnknownValues()
    {
        var samples = BosePayload.Battery([19, 255, 255, 1, 72, 255, 255, 2, 255, 255, 255, 3, 64, 255, 255, 4]);
        Assert.NotNull(samples);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, samples.Select(s => s.Id));
        Assert.Equal(new int?[] { 19, 72, null, 64 }, samples.Select(s => s.Percent));
        Assert.Null(BosePayload.Battery([1, 255, 255, 1, 2, 255, 255, 1]));
        Assert.Null(BosePayload.Battery([80, 255, 255]));
        Assert.Null(BosePayload.Battery([]));
    }

    [Fact]
    public void ModeLayouts_DoNotConfuseGenerationsOrInventModes()
    {
        byte[] qc = ModeFixture(BoseModel.QuietComfort45, 2, 5);
        byte[] ultra = ModeFixture(BoseModel.QuietComfortUltraHeadphones2, 4, 5);
        Assert.Null(BosePayload.Mode(qc, BoseModel.QuietComfortUltraHeadphones2));
        Assert.Null(BosePayload.Mode(ultra, BoseModel.QuietComfort45));
        var custom = BosePayload.Mode(qc, BoseModel.QuietComfort45);
        Assert.NotNull(custom);
        Assert.Equal("Custom", custom.Name);
        Assert.True(custom.Editable);
        Assert.Equal(5, custom.Cnc);
        // A short SET echo is not a full fresh configuration.
        Assert.Null(BosePayload.Mode(qc[..39], BoseModel.QuietComfort45));

        var quiet = BosePayload.Mode(ModeFixture(BoseModel.QuietComfort45, 0), BoseModel.QuietComfort45)!;
        Assert.False(quiet.Configured);
        Assert.Equal(NoiseControlMode.NoiseCancellation, BosePayload.Noise(0, new Dictionary<byte, BoseModeConfig> { [0] = quiet }));
        Assert.Null(BosePayload.Noise(9, new Dictionary<byte, BoseModeConfig> { [0] = quiet }));
        Assert.Null(BosePayload.Noise(2, new Dictionary<byte, BoseModeConfig> { [2] = custom }));
        Assert.Equal(4, BosePayload.ModeCount([4, 0, 3], BoseModel.QuietComfort45));
        Assert.Null(BosePayload.ModeCount([11, 0, 3], BoseModel.QuietComfort45));
        Assert.Equal(11, BosePayload.ModeCount([11, 0, 3], BoseModel.QuietComfortUltraHeadphones2));
        Assert.Null(BosePayload.ModeCount([4, 1, 0], BoseModel.QuietComfort45));
        Assert.Null(BosePayload.ModeCount([4, 0], BoseModel.QuietComfort45));
        Assert.Equal("8.2.20+test", BosePayload.Firmware("8.2.20+test"u8));
        Assert.Null(BosePayload.Firmware("820"u8));
        Assert.Null(BosePayload.Firmware([0x38, 0x2E, 0x01]));
    }

    [Theory]
    [InlineData("Bose QC45", "bose-qc45")]
    [InlineData("Bose QuietComfort 45", "bose-qc45")]
    [InlineData("Bose QC Ultra Headphones 2nd Gen", "bose-qc-ultra-headphones-2")]
    [InlineData("Bose QuietComfort Ultra Headphones (2nd Gen)", "bose-qc-ultra-headphones-2")]
    [InlineData("Bose QuietComfort Ultra Earbuds (2nd Gen)", null)]
    [InlineData("Bose QC Ultra Earbuds 2nd Gen", null)]
    [InlineData("Bose QuietComfort Ultra Headphones", null)]
    [InlineData("Bose QC35 II", null)]
    [InlineData("Bose NC 700", null)]
    public void Names_SelectOnlyThePortedHeadphones(string name, string? expected)
    {
        var registry = new DeviceProfileRegistry(BuiltInProfiles.Create());
        var profile = registry.Match(new DeviceIdentity("test", name, default));
        Assert.Equal(expected, profile?.ModelId);
        if (profile is null) return;
        var bose = Assert.IsType<BoseProfile>(profile);
        Assert.Equal("bose", bose.Brand);
        Assert.Equal(new[] { BoseProfiles.BmapServiceUuid }, bose.ControlServiceUuids);
        Assert.Empty(bose.NoiseControlOptions);
        Assert.Equal(DeviceForm.Headphones, bose.Form);
        var key = new DeviceResourceKey(bose.ModelId, null, null, name, bose.DisplayName);
        Assert.False(key.UsesOplusId);
        Assert.False(key.CanDownload);
    }

    [Fact]
    public async Task Qc45_WakesSessionThenReadsBatteryFirmwareAndModes()
    {
        var device = new Device(BoseModel.QuietComfort45);
        var (controller, store) = Create(device, BoseModel.QuietComfort45); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True(device.Commands[0] is { Block: 0, Function: 1, Operation: BoseOperation.Get });
        Assert.Equal(80, store.Current.Battery.Left!.Value.Level);
        Assert.Null(store.Current.Battery.Left.Value.IsCharging);
        Assert.Null(store.Current.Battery.Right);
        Assert.Equal("8.2.20+test", store.Current.Firmware!.Value.Version);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        var profile = Assert.IsType<BoseProfile>(store.Current.Profile);
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(new[] { NoiseControlMode.NoiseCancellation, NoiseControlMode.Transparency }, profile.NoiseControlOptions.Select(o => o.Mode));
        // CNC level and other writes are not part of this port.
        Assert.DoesNotContain(device.Commands, f => f.Operation is BoseOperation.Set or BoseOperation.SetGet);
    }

    [Fact]
    public async Task ModeSwitch_WaitsForResultThenFreshReadback()
    {
        var device = new Device(BoseModel.QuietComfortUltraHeadphones2);
        var (controller, store) = Create(device, BoseModel.QuietComfortUltraHeadphones2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.DoesNotContain(device.Commands, f => f.Block == 0 && f.Function == 1);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl!.Value.Mode);
        int start = device.Commands.FindLastIndex(f => f.Operation == BoseOperation.Start);
        Assert.Equal(new byte[] { 1, 0 }, device.Commands[start].Payload);
        Assert.True(device.Commands[start + 1] is { Block: 31, Function: 3, Operation: BoseOperation.Get });
        // The preset was re-read before START.
        Assert.True(device.Commands[start - 1] is { Block: 31, Function: 6, Operation: BoseOperation.Get });
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "deep"), Ct)).Status);
    }

    [Fact]
    public async Task IgnoredOrMalformedReadback_IsNotConfirmed()
    {
        var device = new Device(BoseModel.QuietComfortUltraHeadphones2) { IgnoreModeWrite = true };
        var (controller, store) = Create(device, BoseModel.QuietComfortUltraHeadphones2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        // The STATUS push and RESULT echo the unchanged mode; four fresh GETs disagree.
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        device.IgnoreModeWrite = false; device.MalformedReadback = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Null(store.Current.NoiseControl);
    }

    [Fact]
    public async Task RejectedStart_ReportsDeviceError()
    {
        var device = new Device(BoseModel.QuietComfortUltraHeadphones2) { RejectStart = true };
        var (controller, store) = Create(device, BoseModel.QuietComfortUltraHeadphones2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        var result = await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        Assert.False(controller.Closed.IsCompleted);
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task WrongModeLayout_DoesNotOpenNoiseControl()
    {
        var device = new Device(BoseModel.QuietComfortUltraHeadphones2);
        device.Modes[0] = device.Modes[0][..^1];
        var (controller, store) = Create(device, BoseModel.QuietComfortUltraHeadphones2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(80, store.Current.Battery.Left!.Value.Level);
        Assert.Null(store.Current.NoiseControl);
        var profile = Assert.IsType<BoseProfile>(store.Current.Profile);
        Assert.Empty(profile.NoiseControlOptions);
        Assert.Equal(CapabilitySupport.Unknown, profile.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.DoesNotContain(device.Commands, f => f.Operation == BoseOperation.Start);
    }

    [Fact]
    public async Task UserModeAndMissingModeList_StayUnknown()
    {
        var device = new Device(BoseModel.QuietComfortUltraHeadphones2) { Current = 9 };
        var (controller, store) = Create(device, BoseModel.QuietComfortUltraHeadphones2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Null(store.Current.NoiseControl);
        Assert.NotEmpty(store.Current.Profile!.NoiseControlOptions);

        var noList = new Device(BoseModel.QuietComfort45) { ModeCount = [1, 0, 0] };
        var (other, otherStore) = Create(noList, BoseModel.QuietComfort45); await using var second = other;
        Assert.True(await other.InitializeAsync(Ct));
        Assert.Empty(otherStore.Current.Profile!.NoiseControlOptions);
        Assert.DoesNotContain(noList.Commands, f => f.Block == 31 && f.Function == 6);
    }

    [Fact]
    public async Task MalformedBattery_DoesNotEstablishSession()
    {
        var device = new Device(BoseModel.QuietComfort45) { Battery = [80, 255, 255] };
        var (controller, store) = Create(device, BoseModel.QuietComfort45); await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        Assert.Null(store.Current.Battery.Left);
    }

    [Fact]
    public async Task BatteryPush_ReplacesTheWholeReport()
    {
        var device = new Device(BoseModel.QuietComfortUltraHeadphones2);
        var (controller, store) = Create(device, BoseModel.QuietComfortUltraHeadphones2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.Battery = [255, 255, 255, 0];
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Null(store.Current.Battery.Left);
        device.Battery = [0, 255, 255, 0];
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal(0, store.Current.Battery.Left!.Value.Level);
    }
}
