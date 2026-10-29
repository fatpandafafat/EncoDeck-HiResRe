// SPDX-License-Identifier: GPL-3.0-only
// The session order (mutual auth, device info, then one config per read) follows the REDMI Buds 6 Pro
// session log attached to BudsLink PR #110 (https://github.com/maniacx/BudsLink/pull/110, GPL-3.0).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Xiaomi;

namespace EncoDeck.Tests.Protocol.Xiaomi;

public class XiaomiDeviceControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StalledWriteChannel : EncoDeck.Protocol.Transport.IDuplexByteChannel
    {
        private readonly EncoDeck.Tests.Fakes.FakeByteChannel _inner = new();
        public string Description => "stalled Xiaomi control write";
        public int Writes { get; private set; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Writes++;
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        public ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken) => _inner.ReadAsync(data, cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Fact]
    public async Task StalledHandshakeWrite_EndsSessionInsteadOfContinuingWithoutAuth()
    {
        await using var channel = new StalledWriteChannel();
        var identity = new EncoDeck.Core.Devices.DeviceIdentity("stalled-redmi", "REDMI Buds 8 Pro", new EncoDeck.Core.Privacy.BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity });
        await using var controller = XiaomiDeviceProfile.Redmi.CreateController(channel,
            new(identity, store, () => false, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, TimeProvider.System));

        var exception = await Assert.ThrowsAsync<IOException>(() => controller.InitializeAsync(Ct).WaitAsync(TimeSpan.FromSeconds(8), Ct));

        Assert.Contains("write timed out", exception.Message);
        Assert.True(controller.Closed.IsCompleted);
        Assert.Equal(1, channel.Writes);
    }

    [Fact]
    public async Task CancelledHandshakeWrite_PreservesCallerCancellation()
    {
        await using var channel = new StalledWriteChannel();
        var identity = new EncoDeck.Core.Devices.DeviceIdentity("cancelled-redmi", "REDMI Buds 8 Pro", new EncoDeck.Core.Privacy.BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity });
        await using var controller = XiaomiDeviceProfile.Redmi.CreateController(channel,
            new(identity, store, () => false, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, TimeProvider.System));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var initialize = controller.InitializeAsync(cancellation.Token);
        await channel.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        Assert.Equal(1, channel.Writes);
    }

    [Fact]
    public async Task Initialize_RunsMutualAuthAndReadsDeviceInfo()
    {
        await using var buds = new XiaomiFakeEarbuds();
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds);
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));

        var frames = buds.Snapshot();
        // Our challenge goes out first with the auth opcode, then the verdict 01 00.
        var challenge = frames.First();
        Assert.Equal((XiaomiProtocol.KindRequest, XiaomiProtocol.OpAuthCheck), (challenge.Kind, challenge.OpCode));
        Assert.Equal(17, challenge.Payload.Length);
        Assert.Contains(frames, f => f is { Kind: XiaomiProtocol.KindRequest, OpCode: XiaomiProtocol.OpAuthResult } && f.Payload.SequenceEqual(new byte[] { 1, 0 }));
        // The earbuds' own challenge was answered correctly and their verdict acknowledged with 01.
        Assert.True(buds.PeerVerified);
        Assert.Contains(frames, f => f.IsResponse && f.OpCode == XiaomiProtocol.OpAuthResult && XiaomiConfigCodec.NormalizeResponse(f)!.Payload is [1]);
        Assert.True(((XiaomiDeviceController)controller).IsAuthenticated);

        var s = store.Current;
        Assert.Equal(new BatteryReading(95, false), s.Battery.Left!.Value);
        Assert.Equal(new BatteryReading(100, false), s.Battery.Right!.Value);
        Assert.Null(s.Battery.Case); // 0xFF: not reported
        Assert.Equal("1.3.9.9", s.Firmware!.Value.Version);
        Assert.Equal("2717:509E", s.Firmware.Value.Extra["vid-pid"]);
        Assert.Equal(CapabilitySupport.Supported, s.Profile!.Capabilities.Get(DeviceCapability.ChargingStatus).Support);
        Assert.Equal("redmi-earbuds", s.Profile.ModelId);
        Assert.False(s.Toggles[DeviceToggle.GamingMode].Value);
    }

    [Fact]
    public async Task Initialize_WithoutAuthAnswer_StillReadsEarbuds()
    {
        // REDMI Buds 8 Pro (robin-liquidium): the paired RFCOMM connection answers without app-layer auth.
        await using var buds = new XiaomiFakeEarbuds { AnswersAuth = false };
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds, "REDMI Buds 8 Pro");
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));
        Assert.False(((XiaomiDeviceController)controller).IsAuthenticated);
        Assert.NotNull(store.Current.Battery.Left);
        Assert.DoesNotContain(buds.Snapshot(), f => f.OpCode == XiaomiProtocol.OpAuthResult);
    }

    [Fact]
    public async Task Initialize_FailsWhenDeviceInfoNeverArrives()
    {
        await using var buds = new XiaomiFakeEarbuds { TargetInfo = [] };
        var (controller, _) = XiaomiFakeEarbuds.Connect(buds);
        await using var __ = controller;
        Assert.False(await controller.InitializeAsync(Ct));
    }

    [Fact]
    public async Task ConfigsAreReadOneAtATime_AndSilentIdsDoNotBlockTheSession()
    {
        await using var buds = new XiaomiFakeEarbuds();
        buds.Silent.Add(XiaomiConfig.Spatial);
        buds.Values.Remove(XiaomiConfig.PromptVolume); // answered with status 4
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds);
        await using var _ = controller;

        Assert.True(await controller.InitializeAsync(Ct));
        var caps = store.Current.Profile!.Capabilities;
        Assert.NotEqual(CapabilitySupport.Supported, caps.Get(DeviceCapability.SpatialAudio).Support);
        Assert.NotEqual(CapabilitySupport.Supported, caps.Get(DeviceCapability.PromptVolume).Support);
        Assert.Equal(CapabilitySupport.Supported, caps.Get(DeviceCapability.TouchCustomization).Support);
        Assert.Equal(CapabilitySupport.Supported, caps.Get(DeviceCapability.DualDeviceConnection).Support);
    }

    [Fact]
    public async Task SetGamingMode_ConfirmedByReadBack()
    {
        await using var buds = new XiaomiFakeEarbuds();
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct);

        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(new byte[] { 1 }, buds.Values[XiaomiConfig.LowLatency]);
        var toggle = store.Current.Toggles[DeviceToggle.GamingMode];
        Assert.True(toggle.Value);
        Assert.Equal(StateSource.CommandConfirmation, toggle.Source);
        // OpenXia capture shape: F2 [03 00 2F 01].
        Assert.Contains(buds.Snapshot(), f => f.OpCode == XiaomiProtocol.OpSetConfig && f.Payload.SequenceEqual(new byte[] { 3, 0, 0x2F, 1 }));
    }

    [Fact]
    public async Task SetGamingMode_RefusedKeepsDeviceValue()
    {
        await using var buds = new XiaomiFakeEarbuds { AcceptWrites = false };
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.False(store.Current.Toggles[DeviceToggle.GamingMode].Value);
        Assert.NotNull(store.Current.LastError);
    }

    [Fact]
    public async Task WriteRefusedByStatus_IsRejectedWithoutReadback()
    {
        await using var buds = new XiaomiFakeEarbuds { WriteStatus = XiaomiProtocol.StatusUnsupported };
        var (controller, _) = XiaomiFakeEarbuds.Connect(buds);
        await using var __ = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        int reads = buds.Snapshot().Count(f => f.OpCode == XiaomiProtocol.OpGetConfig);

        var result = await controller.SetToggleAsync(DeviceToggle.DualDeviceConnection, false, Ct);

        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Equal(reads, buds.Snapshot().Count(f => f.OpCode == XiaomiProtocol.OpGetConfig));
    }

    [Fact]
    public async Task NoiseControl_NotOfferedWhereFirmwareAnswersWithoutAnc()
    {
        // OpenXia: the Redmi Buds 6 Play answers the noise setting but has no ANC.
        await using var buds = new XiaomiFakeEarbuds();
        var (controller, store) = XiaomiFakeEarbuds.Connect(buds, "Redmi Buds 6 Play");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        Assert.NotEqual(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);
        Assert.Equal(CommandStatus.Unsupported, result.Status);
    }
}
