// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Threading.Channels;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed class SennheiserTw4Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("MOMENTUM TW 4")]
    [InlineData("MOMENTUM TW4")]
    [InlineData("Sennheiser MOMENTUM True Wireless 4")]
    public async Task LiveReadsEnableOnlyTw4ControlsAndRegisterBothNoiseFeatures(string name)
    {
        await using var session = new Session(name);
        Assert.Empty(session.Profile.NoiseControlOptions);
        Assert.Empty(session.Profile.Toggles);
        Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.True(await session.Controller.InitializeAsync(Ct));
        var state = session.Store.Current;
        Assert.Equal("sennheiser-tw4", state.Profile!.ModelId);
        Assert.Equal("5.19.1", state.Firmware?.Value.Version);
        Assert.Equal(80, state.Battery.Left?.Value.Level);
        Assert.Equal(80, state.Battery.Right?.Value.Level);
        Assert.Equal(100, state.Battery.Case?.Value.Level);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation, "normal"), state.NoiseControl?.Value);
        Assert.True(state.Profile.Capabilities.IsAvailable(DeviceCapability.NoiseCancellation, false));
        Assert.True(state.Profile.Capabilities.IsAvailable(DeviceCapability.TouchCustomization, false));
        Assert.Contains(DeviceToggle.InEarDetection, ((IControllableProfile)state.Profile).Toggles);
        Assert.Contains(DeviceToggle.GamingMode, ((IControllableProfile)state.Profile).Toggles);
        Assert.Equal(8, state.KeyFunctions?.Value.Count);
        Assert.Equal(new byte[] { 3, 4, 8, 10, 12, 13, 22 }, session.Device.Requests.Where(r => r.Command == 7).Select(r => r.Payload[0]));
        Assert.DoesNotContain(session.Device.Requests, r => r.Command is 0x1A02 or 0x1A03 or 0x1803);
    }

    [Fact]
    public async Task NoiseWritesUseIndependentStatusesAndConfirmNormalAntiWindTransparencyAndOff()
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));
        foreach (var target in new[]
        {
            new NoiseControlState(NoiseControlMode.Transparency), new(NoiseControlMode.Off),
            new(NoiseControlMode.NoiseCancellation, "anti-wind"), new(NoiseControlMode.NoiseCancellation, "normal"),
        })
        {
            Assert.True((await session.Controller.SetNoiseControlAsync(target, Ct)).IsSuccess);
            Assert.Equal(target, session.Store.Current.NoiseControl!.Value);
        }
        var writes = session.Device.Requests.Where(r => r.Command is 0x1804 or 0x1A04 or 0x1A00).ToArray();
        Assert.Equal(new ushort[] { 0x1804, 0x1804, 0x1A04, 0x1804, 0x1A00, 0x1A04, 0x1804, 0x1A00, 0x1A04 }, writes.Select(r => r.Command));
        Assert.Equal(new byte[] { 1, 1 }, writes[4].Payload);
        Assert.Equal(new byte[] { 1, 0 }, writes[7].Payload);
        int count = session.Device.Requests.Count;
        foreach (var invalid in new[] { new NoiseControlState(NoiseControlMode.Adaptive), new(NoiseControlMode.Transparency, "anti-wind"), new(NoiseControlMode.NoiseCancellation, "deep") })
            Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetNoiseControlAsync(invalid, Ct)).Status);
        Assert.Equal(count, session.Device.Requests.Count);
    }

    [Fact]
    public async Task TransparencyNotificationsDoNotOverwriteRememberedAncAndSavedLevelAndAcksNeverSelectAMode()
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));
        await session.NotifyAsync(0x1A83, [50]);
        Assert.Equal(NoiseControlMode.Transparency, session.Store.Current.NoiseControl?.Value.Mode);
        Assert.Equal(StateSource.DeviceNotification, session.Store.Current.NoiseControl?.Source);
        await session.NotifyAsync(0x1A83, [0]);
        Assert.Equal(NoiseControlMode.NoiseCancellation, session.Store.Current.NoiseControl?.Value.Mode);
        await session.NotifyAsync(0x1A85, [0]);
        Assert.Equal(NoiseControlMode.Off, session.Store.Current.NoiseControl?.Value.Mode);
        var before = session.Store.Current.NoiseControl;
        foreach (var (command, payload) in new (ushort, byte[])[]
        {
            (0x1903, [50]), (0x1B04, []), (0x1904, []), (0x1B85, [1]),
            (0x1A85, [2]), (0x1885, [1, 0]), (0x1A83, [101]),
            (0x1A81, [1, 1, 1, 0, 3, 0]),
        }) await session.NotifyAsync(command, payload);
        Assert.Equal(before, session.Store.Current.NoiseControl);
        await session.NotifyAsync(0x1885, [1]);
        Assert.Equal(NoiseControlMode.Transparency, session.Store.Current.NoiseControl?.Value.Mode);
        await session.NotifyAsync(0x1885, [0]);
        Assert.Equal(NoiseControlMode.Off, session.Store.Current.NoiseControl?.Value.Mode);
    }

    [Fact]
    public async Task RejectedAndIgnoredNoiseWritesReturnFailureAndRenderActualPartialState()
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));
        session.Device.Rejected.Add(0x1804);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, session.Store.Current.NoiseControl?.Value.Mode);
        session.Device.Rejected.Clear();
        session.Device.Ignored.Add(0x1804);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).Status);
        session.Device.Ignored.Clear();
        Assert.True((await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        session.Device.Rejected.Add(0x1A04);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, session.Store.Current.NoiseControl?.Value.Mode);
        session.Device.Rejected.Clear();
        session.Device.Silent.Add(0x1805);
        Assert.Equal(CommandStatus.Timeout, (await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
    }

    [Theory]
    [InlineData(DeviceToggle.InEarDetection, 0x080C, 0x080D)]
    [InlineData(DeviceToggle.GamingMode, 0x0817, 0x0818)]
    public async Task SmartPauseAndLowLatencyRequireAcknowledgementAndExactBooleanReadback(DeviceToggle toggle, int write, int read)
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));
        Assert.True((await session.Controller.SetToggleAsync(toggle, true, Ct)).IsSuccess);
        Assert.True(session.Store.Current.Toggles[toggle].Value);
        Assert.Equal((ushort)read, session.Device.Requests[^1].Command);
        session.Device.Rejected.Add((ushort)write);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetToggleAsync(toggle, false, Ct)).Status);
        Assert.True(session.Store.Current.Toggles[toggle].Value);
        session.Device.Rejected.Clear();
        session.Device.Ignored.Add((ushort)write);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetToggleAsync(toggle, false, Ct)).Status);
        session.Device.Ignored.Clear();
        Assert.True((await session.Controller.SetToggleAsync(toggle, false, Ct)).IsSuccess);
        Assert.False(session.Store.Current.Toggles[toggle].Value);
    }

    [Fact]
    public async Task MissingOrInvalidReadsKeepControlsHiddenAndOtherSennheiserModelsDoNotInheritTw4Writes()
    {
        await using (var session = new Session())
        {
            session.Device.Rejected.UnionWith([0x1805, 0x080D, 0x0818, 0x1601]);
            session.Device.Overrides[0x1A01] = [1, 0, 1, 0, 3, 0];
            Assert.True(await session.Controller.InitializeAsync(Ct));
            Assert.Empty(session.Store.Current.Profile!.NoiseControlOptions);
            Assert.Empty(((IControllableProfile)session.Store.Current.Profile).Toggles);
            Assert.False(session.Store.Current.Profile.Capabilities.IsAvailable(DeviceCapability.TouchCustomization, false));
            int count = session.Device.Requests.Count;
            Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetToggleAsync(DeviceToggle.GamingMode, true, Ct)).Status);
            Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetKeyFunctionAsync(new(EarSide.Left, 0, GestureAction.SingleSqueeze, 1), Ct)).Status);
            Assert.Equal(count, session.Device.Requests.Count);
        }
        await using var tw3 = new Session("MOMENTUM TW 3");
        Assert.True(await tw3.Controller.InitializeAsync(Ct));
        Assert.Empty(tw3.Store.Current.Profile!.NoiseControlOptions);
        Assert.Equal(CommandStatus.Unsupported, (await tw3.Controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.DoesNotContain(tw3.Device.Requests, r => r.Command is 0x1A05 or 0x1805 or 0x1601);
    }

    [Fact]
    public async Task InvalidBooleanCannotEnableLowLatencyAndMissingWindDoesNotBlockBasicNoiseControl()
    {
        await using var session = new Session();
        session.Device.Overrides[0x0818] = [2];
        session.Device.Rejected.Add(0x1A01);
        Assert.True(await session.Controller.InitializeAsync(Ct));
        var state = session.Store.Current;
        Assert.False(state.Toggles.ContainsKey(DeviceToggle.GamingMode));
        Assert.DoesNotContain(DeviceToggle.GamingMode, ((IControllableProfile)state.Profile!).Toggles);
        Assert.DoesNotContain(state.Profile!.NoiseControlOptions, o => o.LevelId is not null);
        Assert.True((await session.Controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(new NoiseControlState(NoiseControlMode.NoiseCancellation), session.Store.Current.NoiseControl?.Value);
        Assert.DoesNotContain(session.Device.Requests, r => r.Command == 0x1A00);
    }

    [Fact]
    public async Task GesturesUsePhysicalSidesAndPatternEchoAndRestrictActionsWithoutChangingOtherSlots()
    {
        await using var session = new Session();
        session.Device.Keys[(1, 3)] = 222; // Unknown device value remains visible and unmodified.
        Assert.True(await session.Controller.InitializeAsync(Ct));
        Assert.Equal(222, session.Store.Current.KeyFunctions!.Value.Single(k => k.Side == EarSide.Left && k.Action == GestureAction.TripleSqueeze).Function);
        var target = new KeyFunction(EarSide.Right, 0, GestureAction.SqueezeAndHold, 4);
        Assert.True((await session.Controller.SetKeyFunctionAsync(target, Ct)).IsSuccess);
        Assert.Contains(target, session.Store.Current.KeyFunctions!.Value);
        Assert.Equal(new byte[] { 0, 4, 4 }, session.Device.Requests.Last(r => r.Command == 0x1600).Payload);
        Assert.Equal(222, session.Device.Keys[(1, 3)]);
        int count = session.Device.Requests.Count;
        foreach (var invalid in new[]
        {
            target with { Side = EarSide.Both }, target with { Button = KeyFunction.CallButton },
            target with { Action = GestureAction.SingleSqueeze }, target with { Function = 2 },
            target with { Action = GestureAction.LongHold }, target with { Function = 222 },
        }) Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetKeyFunctionAsync(invalid, Ct)).Status);
        Assert.Equal(count, session.Device.Requests.Count);
        session.Device.Rejected.Add(0x1600);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetKeyFunctionAsync(target with { Function = 5 }, Ct)).Status);
        Assert.Contains(target, session.Store.Current.KeyFunctions!.Value);
        session.Device.Rejected.Clear();
        session.Device.Ignored.Add(0x1600);
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SetKeyFunctionAsync(target with { Function = 5 }, Ct)).Status);
        session.Device.Ignored.Clear();
        session.Device.WrongGestureEcho = true;
        int writes = session.Device.Requests.Count(r => r.Command == 0x1600);
        Assert.Equal(CommandStatus.Timeout, (await session.Controller.SetKeyFunctionAsync(target with { Function = 5 }, Ct)).Status);
        Assert.Equal(writes, session.Device.Requests.Count(r => r.Command == 0x1600));
    }

    private sealed class Session : IAsyncDisposable
    {
        public Tw4Channel Device { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public SennheiserProfile Profile { get; }
        public IDeviceController Controller { get; }
        private byte _fence = 5;
        public Session(string name = "MOMENTUM TW 4")
        {
            var identity = new DeviceIdentity("tw4-test", name, new BluetoothAddress(0));
            Profile = (SennheiserProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
            Store.Reset(DeviceState.Empty with { Device = identity, Profile = Profile });
            Controller = Profile.CreateController(Device, new(identity, Store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        }
        public async Task NotifyAsync(ushort command, byte[] payload)
        {
            byte fence = ++_fence;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, DeviceStateChangedEventArgs args)
            {
                if (args.Current.Battery.Left?.Value.Level == fence) done.TrySetResult();
            }
            Store.StateChanged += Changed;
            try
            {
                Device.Emit(command, payload);
                Device.Emit(0x0683, [fence, 80, 100]); // Ordered marker ensures the previous frame has been handled.
                await done.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
            }
            finally { Store.StateChanged -= Changed; }
        }
        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    /// <summary>TW4 byte fixtures from tw4.json plus the published on-device ANC/transparency sequences.</summary>
    private sealed class Tw4Channel : IDuplexByteChannel
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        public string Description => "TW4 GAIA v3 simulator";
        public List<(ushort Command, byte[] Payload)> Requests { get; } = [];
        public HashSet<ushort> Rejected { get; } = [];
        public HashSet<ushort> Ignored { get; } = [];
        public HashSet<ushort> Silent { get; } = [];
        public Dictionary<ushort, byte[]> Overrides { get; } = [];
        public Dictionary<(byte Side, byte Pattern), byte> Keys { get; } = new()
        {
            [(1, 1)] = 6, [(1, 2)] = 3, [(1, 3)] = 9, [(1, 4)] = 5,
            [(0, 1)] = 1, [(0, 2)] = 2, [(0, 3)] = 7, [(0, 4)] = 4,
        };
        public bool WrongGestureEcho { get; set; }
        private byte _anc = 1, _transparency, _wind, _pause, _latency;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            byte[] bytes = data.ToArray();
            ushort command = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6));
            byte[] payload = bytes[8..];
            Requests.Add((command, payload));
            if (Silent.Contains(command)) return ValueTask.CompletedTask;
            bool error = Rejected.Contains(command);
            byte[]? response = command switch
            {
                0x0007 => [], 0x0603 => [80, 80, 100], 0x0602 => [0, 0],
                0x1201 => [0, 5, 0, 19, 0, 1], 0x1206 => "MTW4"u8.ToArray(),
                0x1A05 => [_anc], 0x1805 => [_transparency], 0x1A01 => [1, _wind, 2, 0, 3, 0],
                0x080D => [_pause], 0x0818 => [_latency],
                0x1601 => [WrongGestureEcho ? (byte)(1 - payload[0]) : payload[0], payload[1], Keys[(payload[0], payload[1])]],
                0x1A04 or 0x1804 or 0x1A00 or 0x080C or 0x0817 or 0x1600 => [],
                _ => null,
            };
            error |= response is null;
            if (!error && !Ignored.Contains(command))
            {
                switch (command)
                {
                    case 0x1A04: _anc = payload[0]; break;
                    case 0x1804: _transparency = payload[0]; break;
                    case 0x1A00: Assert.Equal(1, payload[0]); _wind = payload[1]; break;
                    case 0x080C: _pause = payload[0]; break;
                    case 0x0817: _latency = payload[0]; break;
                    case 0x1600: Keys[(payload[0], payload[1])] = payload[2]; break;
                }
            }
            Emit((ushort)(command | (error ? 0x180 : 0x100)), error ? [1] : Overrides.GetValueOrDefault(command, response!));
            return ValueTask.CompletedTask;
        }

        public void Emit(ushort command, byte[] payload)
        {
            byte[] frame = SennheiserFrameCodec.Encode(command, payload);
            frame[1] = 4; // TW4's real replies also use v4 without extended-length flags.
            _incoming.Writer.TryWrite(frame[..5]);
            _incoming.Writer.TryWrite(frame[5..]);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try { var bytes = await _incoming.Reader.ReadAsync(cancellationToken); bytes.CopyTo(buffer); return bytes.Length; }
            catch (ChannelClosedException) { return 0; }
        }
        public ValueTask DisposeAsync() { _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
