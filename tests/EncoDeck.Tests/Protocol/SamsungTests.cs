// SPDX-License-Identifier: GPL-3.0-only
// Vectors ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Samsung;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public class SamsungTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal));

    /// <summary>Simulated Galaxy Buds: answers 0x61 with its extended status and applies writes to it.</summary>
    private sealed class Device : IDuplexByteChannel
    {
        private readonly FakeByteChannel _inner = new();
        public string Description => "Galaxy Buds SPP fixture";
        public List<SamsungFrame> Writes { get; } = [];
        public byte[] Status;
        public bool IgnoreWrites;
        public Func<SamsungFrame, SamsungFrame[]?>? Override;

        public Device(SamsungModel model)
        {
            byte revision = model switch { SamsungModel.Buds2 => 10, SamsungModel.Buds2Pro => 13, _ => 0 };
            Status = new byte[SamsungProfiles.MinimumStatusLength(model, revision)];
            Status[0] = revision; Status[2] = 70; Status[3] = 60; Status[7] = 50;
            Status[9] = 1; Status[10] = 0x9A; Status[12] = 1; Status[24] = 2;
            if (model == SamsungModel.Buds2) Status[36] = 0x14;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _inner.WriteAsync(data, ct);
            foreach (var frame in new SamsungFrameDecoder().Feed(data.Span))
            {
                Writes.Add(frame);
                var replies = Override?.Invoke(frame) ?? Default(frame);
                foreach (var reply in replies) _inner.Receive(SamsungFrameCodec.Encode(reply));
            }
        }

        private SamsungFrame[] Default(SamsungFrame frame)
        {
            if (frame.Command == 0x61) return [new(0x61, Status.ToArray())];
            if (IgnoreWrites) return [];
            switch (frame.Command)
            {
                case 0x78: Status[12] = frame.Payload[0]; break;
                case 0x86: Status[9] = frame.Payload[0]; break;
                case 0x83: Status[24] = frame.Payload[0]; break;
                case 0x90:
                    int[] bits = [3, 2, 1, 0, 4, 5];
                    Status[10] = frame.Payload[0] == 0 ? (byte)0 : (byte)0x80;
                    for (int i = 1; i < frame.Payload.Length; i++) Status[10] |= (byte)(frame.Payload[i] << bits[i - 1]);
                    break;
            }
            return [];
        }

        public void Push(SamsungFrame frame) => _inner.Receive(SamsungFrameCodec.Encode(frame));
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(Device device, SamsungModel model)
    {
        var profile = (SamsungProfile)SamsungProfiles.CreateBuiltIn().Single(p => ((SamsungProfile)p).Model == model);
        var identity = new DeviceIdentity("samsung-test", profile.DisplayName, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System)), store);
    }

    // Wire constants independently calculated with CRC-16/XMODEM (HeyBuds' golden vectors).
    [Theory]
    [InlineData(0x61, "", false, "FD 03 00 61 87 7C DD")]
    [InlineData(0x51, "00", false, "FD 04 00 51 00 8E 3D DD")]
    [InlineData(0x88, "01010001", false, "FD 07 00 88 01 01 00 01 58 71 DD")]
    [InlineData(0x78, "01", false, "FD 04 00 78 01 D1 91 DD")]
    [InlineData(0x86, "05", false, "FD 04 00 86 05 9B E1 DD")]
    [InlineData(0x51, "6100", false, "FD 05 00 51 61 00 E5 51 DD")]
    [InlineData(0x61, "", true, "FD 03 10 61 87 7C DD")]
    public void Frames_MatchGoldenBytesAtEveryStreamSplit(byte command, string payload, bool response, string wire)
    {
        var frame = new SamsungFrame(command, Hex(payload), response);
        byte[] bytes = Hex(wire);
        Assert.Equal(bytes, SamsungFrameCodec.Encode(frame));
        for (int split = 0; split <= bytes.Length; split++)
        {
            var decoder = new SamsungFrameDecoder();
            var frames = decoder.Feed([0, 1, 2, .. bytes[..split]]).Concat(decoder.Feed([.. bytes[split..], .. bytes])).ToList();
            Assert.Equal(new[] { frame, frame }, frames);
        }
    }

    [Theory]
    [InlineData("FD 03 00 61 86 7C DD")] // bad CRC
    [InlineData("FD 03 00 61 87 7C EE")] // legacy terminator
    [InlineData("FD 03 20 61 87 7C DD")] // firmware fragment
    [InlineData("FD 03 04 61 87 7C DD")] // reserved header bit
    [InlineData("FD 02 00 61 87 7C DD")] // no room for id and CRC
    [InlineData("FE 00 03 61 87 7C EE")] // original legacy framing
    public void MalformedFrames_ResynchronizeOnTheNextValidFrame(string malformed)
    {
        Assert.Equal(0x31C3, SamsungFrameCodec.Checksum("123456789"u8));
        var frames = new SamsungFrameDecoder().Feed([.. Hex(malformed), .. Hex("FD 03 00 61 87 7C DD")]);
        Assert.Equal(new[] { new SamsungFrame(0x61, []) }, frames);
    }

    [Theory]
    [InlineData(SamsungModel.Buds2, 0, 27)]
    [InlineData(SamsungModel.Buds2, 2, 28)]
    [InlineData(SamsungModel.Buds2, 3, 29)]
    [InlineData(SamsungModel.Buds2, 5, 33)]
    [InlineData(SamsungModel.Buds2, 6, 34)]
    [InlineData(SamsungModel.Buds2, 7, 35)]
    [InlineData(SamsungModel.Buds2, 8, 36)]
    [InlineData(SamsungModel.Buds2, 10, 37)]
    [InlineData(SamsungModel.Buds2Pro, 0, 34)]
    [InlineData(SamsungModel.Buds2Pro, 1, 41)]
    [InlineData(SamsungModel.Buds2Pro, 3, 42)]
    [InlineData(SamsungModel.Buds2Pro, 8, 43)]
    [InlineData(SamsungModel.Buds2Pro, 11, 44)]
    [InlineData(SamsungModel.Buds2Pro, 13, 45)]
    [InlineData(SamsungModel.Buds4Pro, 0, 25)]
    public void Status_RevisionLengthsAndChargeOffsets(SamsungModel model, byte revision, int length)
    {
        var p = new byte[length];
        p[0] = revision; p[2] = 0; p[3] = 100; p[7] = 255; p[9] = 3; p[10] = 0x9A; p[12] = 1;
        bool charging = (model == SamsungModel.Buds2 && revision >= 10) || (model == SamsungModel.Buds2Pro && revision >= 11);
        if (model == SamsungModel.Buds2 && revision >= 10) p[36] = 0x14;
        if (model == SamsungModel.Buds2Pro && revision >= 11) p[43] = 0x14;
        Assert.Equal(length, SamsungProfiles.MinimumStatusLength(model, revision));
        var status = Assert.IsType<SamsungStatus>(SamsungProfiles.ParseStatus(model, p));
        Assert.Equal(((byte)0, (byte)100, (byte)255), (status.Left, status.Right, status.Case));
        Assert.Equal(NoiseControlMode.NoiseCancellation, status.Noise);
        Assert.Equal((byte)3, status.Equalizer);
        Assert.Equal((byte)0x9A, status.TouchFlags);
        Assert.Equal(charging ? (true, true, false) : ((bool, bool, bool)?)null, status.Charging);
        for (int n = 0; n < length; n++) Assert.Null(SamsungProfiles.ParseStatus(model, p[..n]));
        p[9] = 7;
        Assert.Null(SamsungProfiles.ParseStatus(model, p)!.Equalizer);
        p[12] = model == SamsungModel.Buds4Pro ? (byte)4 : (byte)3;
        Assert.Null(SamsungProfiles.ParseStatus(model, p));
        Assert.Equal(model == SamsungModel.Buds4Pro ? (byte?)3 : null, SamsungProfiles.NoiseByte(model, NoiseControlMode.Adaptive));
    }

    [Theory]
    [InlineData(SamsungModel.Buds2, 6, false)]
    [InlineData(SamsungModel.Buds2, 7, true)]
    [InlineData(SamsungModel.Buds2Pro, 0, false)]
    [InlineData(SamsungModel.Buds2Pro, 1, true)]
    public void TouchLock_PreservesGesturesAndGatesCallGesturesByRevision(SamsungModel model, byte revision, bool calls)
    {
        foreach (byte flags in new byte[] { 0, 0x9A, 0xA5, 0xBF })
        {
            var current = new SamsungStatus(revision, 0, 0, 0, null, NoiseControlMode.Off, null, flags, null);
            byte[] gestures = [(byte)((flags >> 3) & 1), (byte)((flags >> 2) & 1), (byte)((flags >> 1) & 1), (byte)(flags & 1)];
            byte[] expected = calls ? [.. gestures, (byte)((flags >> 4) & 1), (byte)((flags >> 5) & 1)] : gestures;
            Assert.Equal((byte[])[0, .. expected], SamsungProfiles.TouchLockPayload(model, true, current));
            Assert.Equal((byte[])[1, .. expected], SamsungProfiles.TouchLockPayload(model, false, current));
            Assert.Equal((flags & 0x80) == 0, current.TouchLocked);
        }
        Assert.Null(SamsungProfiles.TouchLockPayload(SamsungModel.Buds4Pro, true, new(0, 0, 0, 0, null, NoiseControlMode.Off, null, 0, 2)));
        var p = new byte[25];
        foreach (byte level in new byte[] { 0, 1, 2, 3, 4, 5, 255 })
        {
            p[24] = level;
            Assert.Equal(level <= 4 ? (byte?)level : null, SamsungProfiles.ParseStatus(SamsungModel.Buds4Pro, p)?.AncLevel);
        }
    }

    [Theory]
    [InlineData("Galaxy Buds2", "samsung-buds2")]
    [InlineData("Samsung Galaxy Buds2 (A1b2)", "samsung-buds2")]
    [InlineData("Galaxy Buds2 Pro", "samsung-buds2-pro")]
    [InlineData("Galaxy Buds4 Pro", "samsung-buds4-pro")]
    [InlineData("Galaxy Buds2 FE", null)]
    [InlineData("Galaxy Buds2 Pro Max", null)]
    [InlineData("Galaxy Buds4", null)]
    [InlineData("Galaxy Buds FE", null)]
    [InlineData("Galaxy Buds2 (test)", null)]
    public void Names_MatchExactModelsOnly(string name, string? expected)
    {
        var matched = new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(new("test", name, default));
        Assert.Equal(expected, matched?.ModelId);
        if (matched is null) return;
        var key = new DeviceResourceKey(matched.ModelId, null, null, name, matched.DisplayName);
        Assert.False(key.UsesOplusId);
        Assert.False(key.CanDownload);
        Assert.Equal(new[] { SamsungProfiles.ServiceId }, matched.ControlServiceUuids);
    }

    [Theory]
    [InlineData(SamsungModel.Buds2)]
    [InlineData(SamsungModel.Buds2Pro)]
    [InlineData(SamsungModel.Buds4Pro)]
    public async Task Session_UsesHandshakeAcksStatusAndConfirmsWritesByReadback(SamsungModel model)
    {
        var device = new Device(model);
        var (controller, store) = Create(device, model); await using var session = controller;
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
        Assert.Empty(store.Current.Profile.EqualizerPresets);
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(new[] { new SamsungFrame(0x51, [0]), new SamsungFrame(0x88, [1, 1, 0, 1]), new SamsungFrame(0x61, []) }, device.Writes.Take(3));
        Assert.Contains(new SamsungFrame(0x51, [0x61, 0]), device.Writes);
        Assert.Equal(70, store.Current.Battery.Left!.Value.Level);
        Assert.Equal(60, store.Current.Battery.Right!.Value.Level);
        Assert.Equal(50, store.Current.Battery.Case!.Value.Level);
        Assert.Equal(model switch { SamsungModel.Buds2 => true, SamsungModel.Buds2Pro => false, _ => (bool?)null }, store.Current.Battery.Left.Value.IsCharging);
        var profile = Assert.IsType<SamsungProfile>(store.Current.Profile);
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5" }, profile.EqualizerPresets.Select(p => p.Id));
        Assert.Equal(model == SamsungModel.Buds4Pro, profile.NoiseControlOptions.Any(o => o.Mode == NoiseControlMode.Adaptive));
        Assert.Equal(model != SamsungModel.Buds4Pro, profile.Toggles.Contains(DeviceToggle.TouchControls));

        int baseline = device.Writes.Count;
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(new SamsungFrame(0x78, [2]), device.Writes[baseline]);
        Assert.Contains(new SamsungFrame(0x61, []), device.Writes.Skip(baseline));
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl!.Value.Mode);
        Assert.True((await controller.SetEqualizerPresetAsync("5", Ct)).IsSuccess);
        Assert.Equal("5", store.Current.Equalizer!.Value.ActivePresetId);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("6", Ct)).Status);
    }

    [Fact]
    public async Task Buds4Pro_AncLevelUsesOffset24AndAdaptiveMode()
    {
        var device = new Device(SamsungModel.Buds4Pro);
        var (controller, store) = Create(device, SamsungModel.Buds4Pro); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "level3"), store.Current.NoiseControl!.Value);
        Assert.Equal(5, store.Current.Profile!.NoiseControlOptions.Count(o => o.LevelId is not null));
        Assert.Null(store.Current.Battery.Left!.Value.IsCharging);

        int baseline = device.Writes.Count;
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "level5"), Ct)).IsSuccess);
        // Already in ANC: only the level is written.
        Assert.Equal(new SamsungFrame(0x83, [4]), device.Writes[baseline]);
        Assert.DoesNotContain(device.Writes.Skip(baseline), f => f.Command == 0x78);
        Assert.Equal("level5", store.Current.NoiseControl!.Value.LevelId);

        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Adaptive), Ct)).IsSuccess);
        Assert.Equal(NoiseControlMode.Adaptive, store.Current.NoiseControl!.Value.Mode);
        baseline = device.Writes.Count;
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "level1"), Ct)).IsSuccess);
        Assert.Equal(new[] { new SamsungFrame(0x78, [1]), new SamsungFrame(0x83, [0]) }, device.Writes.Skip(baseline).Take(2));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation, "level6"), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.TouchControls, false, Ct)).Status);
        Assert.DoesNotContain(device.Writes, f => f.Command == 0x90);
    }

    [Theory]
    [InlineData(SamsungModel.Buds2)]
    [InlineData(SamsungModel.Buds2Pro)]
    public async Task TouchLock_UsesFreshFlags(SamsungModel model)
    {
        var device = new Device(model);
        var (controller, store) = Create(device, model); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.True(store.Current.Toggles[DeviceToggle.TouchControls].Value); // touch not locked
        device.Status[10] = 0xA5; // changed on the phone since the last cached status
        Assert.True((await controller.SetToggleAsync(DeviceToggle.TouchControls, false, Ct)).IsSuccess); // lock touch
        Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0, 1 }, device.Writes.First(f => f.Command == 0x90).Payload);
        Assert.Equal((byte)0x25, device.Status[10]);
        Assert.False(store.Current.Toggles[DeviceToggle.TouchControls].Value);
    }

    [Fact]
    public async Task IgnoredWrite_IsRejectedAndMalformedStatusWithdrawsControls()
    {
        var device = new Device(SamsungModel.Buds2);
        var (controller, store) = Create(device, SamsungModel.Buds2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.IgnoreWrites = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);

        // A truncated fresh status must not confirm the cached ANC.
        device.Override = f => f.Command == 0x61 ? [new SamsungFrame(0x61, [10, 0, 70])] : null;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
        Assert.Null(store.Current.NoiseControl);
        Assert.Null(store.Current.Equalizer);
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
        Assert.False(store.Current.Toggles.ContainsKey(DeviceToggle.TouchControls));
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("1", Ct)).Status);

        device.Override = null;
        Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
        Assert.Equal("1", store.Current.Equalizer!.Value.ActivePresetId);
    }

    [Fact]
    public async Task GenericAckAndNoiseEcho_CannotSubstituteForStatus()
    {
        var device = new Device(SamsungModel.Buds2);
        var (controller, store) = Create(device, SamsungModel.Buds2); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.Override = f => f.Command is 0x78 or 0x61 ? [new SamsungFrame(0x51, [0x78, 0]), new SamsungFrame(0x77, [1])] : null;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        // Without request ids the session cannot be reused after a timeout.
        Assert.True(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task Notifications_UpdateBatteryAndNoiseButMalformedStartFails()
    {
        var device = new Device(SamsungModel.Buds2Pro);
        var (controller, store) = Create(device, SamsungModel.Buds2Pro); await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.Push(new SamsungFrame(0x60, [0, 40, 255, 0, 0, 0, 30]));
        device.Push(new SamsungFrame(0x77, [0]));
        await WaitFor(() => store.Current.NoiseControl?.Value.Mode == NoiseControlMode.Off);
        Assert.Equal(40, store.Current.Battery.Left!.Value.Level);
        Assert.Null(store.Current.Battery.Right);
        Assert.Equal(30, store.Current.Battery.Case!.Value.Level);

        var bad = new Device(SamsungModel.Buds2) { Override = f => f.Command == 0x61 ? [new SamsungFrame(0x61, [10, 0, 70])] : null };
        var (failing, failed) = Create(bad, SamsungModel.Buds2); await using var other = failing;
        Assert.False(await failing.InitializeAsync(Ct));
        Assert.Empty(failed.Current.Profile!.NoiseControlOptions);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition());
    }
}
