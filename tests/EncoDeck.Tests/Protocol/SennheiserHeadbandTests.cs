// SPDX-License-Identifier: GPL-3.0-only
// Byte fixtures are the MOMENTUM 4 firmware 3.37.3 captures published in Ariazonaa/momentum4-control
// tests/CapturedPackets (MIT, Copyright (c) 2026 Ariazonaa): EQ config 05 C4 3C 00 00, band frequencies
// 90 / 325 / 1500 / 6500 / 6500 Hz, gains 3C 3C 16 16 00, the four-entry paired list and connect 0x1402 [02].
using System.Buffers.Binary;
using System.Text;
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

public sealed class SennheiserHeadbandTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Momentum4ReadsUserEqSoundModeAndPairedHostsAfterRegistration()
    {
        await using var session = new Session();
        Assert.Null(session.Profile.BandEqualizer);
        Assert.True(await session.Controller.InitializeAsync(Ct));
        var state = session.Store.Current;
        var profile = (SennheiserProfile)state.Profile!;
        Assert.Equal("3.37.3", state.Firmware?.Value.Version);
        Assert.Equal("M4AEBT Black", state.Firmware?.Value.ModelCode);
        Assert.Equal(new byte[] { 3, 4, 8, 10, 12, 13 }, session.Device.Requests.Where(r => r.Command == 7).Select(r => r.Payload[0]));

        Assert.Equal(new BandEqualizerSpec(-60, 60), profile.BandEqualizer);
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.CustomEqualizer, false));
        Assert.Equal(new[] { SennheiserProfile.SoundModePreset(0), SennheiserProfile.SoundModePreset(2) }, profile.EqualizerPresets.Select(p => p.Id));
        var curve = Assert.Single(state.CustomEqualizers!.Value);
        Assert.True(curve.IsSelected);
        Assert.Equal(string.Empty, curve.Name);
        Assert.Equal(new[] { 90, 325, 1500, 6500, 6500 }, curve.Bands.Select(b => b.FrequencyHz));
        Assert.Equal(new[] { 60, 60, 22, 22, 0 }, curve.Bands.Select(b => b.Gain));
        Assert.Equal(new EqualizerState(null, 1), state.Equalizer?.Value);

        Assert.True(profile.CanConnectPairedDevices);
        Assert.True(profile.Capabilities.IsAvailable(DeviceCapability.DualDeviceConnection, false));
        var hosts = state.SourceDevices!.Value;
        Assert.Equal(new[] { "PC-A", "Laptop-B", "Phone-C", "PC-D" }, hosts.Select(h => h.Name));
        Assert.True(hosts[0].IsThisDevice && hosts[0].IsConnected);
        Assert.All(hosts.Skip(1), h => Assert.False(h.IsConnected));
        Assert.Equal(new byte[] { 2 }, hosts[2].Address);
    }

    [Fact]
    public async Task EqWritesOnlyChangedBandsAndRequiresExactReadback()
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));
        var curve = session.Store.Current.CustomEqualizers!.Value[0];
        var target = curve with { Bands = [.. curve.Bands.Select((b, i) => i == 4 ? b with { Gain = 10 } : b)] };

        Assert.True((await session.Controller.SaveCustomEqualizerAsync(target, Ct)).IsSuccess);
        var write = Assert.Single(session.Device.Requests, r => r.Command == 0x1001);
        Assert.Equal(new byte[] { 4, 0x0A }, write.Payload);
        Assert.Equal(10, session.Store.Current.CustomEqualizers!.Value[0].Bands[4].Gain);

        int count = session.Device.Requests.Count;
        var outside = target with { Bands = [.. target.Bands.Select((b, i) => i == 0 ? b with { Gain = 61 } : b)] };
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SaveCustomEqualizerAsync(outside, Ct)).Status);
        var reordered = target with { Bands = [.. target.Bands.Reverse()] };
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SaveCustomEqualizerAsync(reordered, Ct)).Status);
        Assert.Equal(count, session.Device.Requests.Count);

        session.Device.Ignored.Add(0x1001);
        var lower = target with { Bands = [.. target.Bands.Select((b, i) => i == 2 ? b with { Gain = -30 } : b)] };
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.SaveCustomEqualizerAsync(lower, Ct)).Status);
        Assert.Equal(22, session.Store.Current.CustomEqualizers!.Value[0].Bands[2].Gain);
    }

    [Fact]
    public async Task SoundModesAndCurveSelectionAreConfirmedByReadback()
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));
        Assert.True((await session.Controller.SetEqualizerPresetAsync(SennheiserProfile.SoundModePreset(2), Ct)).IsSuccess);
        Assert.Equal(2, session.Device.SoundMode);
        Assert.Equal(new EqualizerState(SennheiserProfile.SoundModePreset(2), 2), session.Store.Current.Equalizer?.Value);
        Assert.False(session.Store.Current.CustomEqualizers!.Value[0].IsSelected);
        Assert.True((await session.Controller.SelectCustomEqualizerAsync(SennheiserController.UserCurveId, Ct)).IsSuccess);
        Assert.Equal(1, session.Device.SoundMode);
        Assert.True(session.Store.Current.CustomEqualizers!.Value[0].IsSelected);

        int count = session.Device.Requests.Count;
        Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetEqualizerPresetAsync(SennheiserProfile.SoundModePreset(3), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SelectCustomEqualizerAsync(7, Ct)).Status);
        Assert.Equal(count, session.Device.Requests.Count);
    }

    [Fact]
    public async Task Firmware2NeverQueriesOrOffersTheUserEq()
    {
        await using var session = new Session();
        session.Device.Firmware = [0, 2, 0, 21, 0, 0];
        Assert.True(await session.Controller.InitializeAsync(Ct));
        var profile = (SennheiserProfile)session.Store.Current.Profile!;
        Assert.Null(profile.BandEqualizer);
        Assert.Empty(profile.EqualizerPresets);
        Assert.False(profile.Capabilities.IsAvailable(DeviceCapability.CustomEqualizer, true));
        Assert.DoesNotContain(session.Device.Requests, r => r.Command is 0x1000 or 0x1003 or 0x0804);
        Assert.Equal(CommandStatus.Unsupported, (await session.Controller.SetEqualizerPresetAsync(SennheiserProfile.SoundModePreset(0), Ct)).Status);
    }

    [Fact]
    public async Task ConnectVerifiesAFreshListAndNeverSendsDisconnectOrDelete()
    {
        await using var session = new Session();
        Assert.True(await session.Controller.InitializeAsync(Ct));

        Assert.True((await session.Controller.MultiConnectAsync(MultiConnectOperation.Connect, [2], Ct)).IsSuccess);
        Assert.Equal(new byte[] { 2 }, Assert.Single(session.Device.Requests, r => r.Command == 0x1402).Payload);
        Assert.True(session.Store.Current.SourceDevices!.Value[2].IsConnected);

        foreach (var operation in new[] { MultiConnectOperation.Disconnect, MultiConnectOperation.Remove })
            Assert.Equal(CommandStatus.Unsupported, (await session.Controller.MultiConnectAsync(operation, [2], Ct)).Status);
        // Both slots are now in use: a third host is refused instead of dropping one.
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.MultiConnectAsync(MultiConnectOperation.Connect, [1], Ct)).Status);
        // The controlling host itself and a renamed entry are refused as well.
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.MultiConnectAsync(MultiConnectOperation.Connect, [0], Ct)).Status);
        session.Device.Hosts[3] = session.Device.Hosts[3] with { Name = "Other" };
        Assert.Equal(CommandStatus.Rejected, (await session.Controller.MultiConnectAsync(MultiConnectOperation.Connect, [3], Ct)).Status);

        Assert.Single(session.Device.Requests, r => r.Command == 0x1402);
        Assert.DoesNotContain(session.Device.Requests, r => r.Command is 0x1403 or 0x1405 or 0x1406);
    }

    [Fact]
    public async Task AccentumListsHostsButCannotConnectThemAndHasNoUserEq()
    {
        await using var session = new Session("ACCENTUM Wireless");
        Assert.True(await session.Controller.InitializeAsync(Ct));
        var profile = (SennheiserProfile)session.Store.Current.Profile!;
        Assert.False(profile.CanConnectPairedDevices);
        Assert.Equal(4, session.Store.Current.SourceDevices?.Value.Count);
        Assert.Null(profile.BandEqualizer);
        Assert.DoesNotContain(session.Device.Requests, r => r.Command == 0x1000);
        Assert.Equal(CommandStatus.Unsupported, (await session.Controller.MultiConnectAsync(MultiConnectOperation.Connect, [2], Ct)).Status);
        Assert.DoesNotContain(session.Device.Requests, r => r.Command is >= 0x1402 and <= 0x1406 and not 0x1404);
    }

    [Fact]
    public void ModelCodeDropsTheNulTerminatorAndRejectsControlBytes()
    {
        Assert.Equal("M4AEBT Black", SennheiserController.ModelCode([.. "M4AEBT Black"u8, 0]));
        Assert.Null(SennheiserController.ModelCode([0x41, 0x07, 0x42]));
        Assert.Null(SennheiserController.ModelCode([0]));
    }

    private sealed class Session : IAsyncDisposable
    {
        public HeadbandChannel Device { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public SennheiserProfile Profile { get; }
        public IDeviceController Controller { get; }
        public Session(string name = "MOMENTUM 4 Wireless")
        {
            var identity = new DeviceIdentity("m4-test", name, new BluetoothAddress(0));
            Profile = (SennheiserProfile)new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(identity)!;
            Store.Reset(DeviceState.Empty with { Device = identity, Profile = Profile });
            Controller = Profile.CreateController(Device, new(identity, Store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        }
        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    private sealed record Host(byte Priority, bool Connected, string Name);

    /// <summary>MOMENTUM 4 headband simulator built from the published firmware 3.37.3 captures.</summary>
    private sealed class HeadbandChannel : IDuplexByteChannel
    {
        private static readonly int[] Frequencies = [90, 325, 1500, 6500, 6500];
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly sbyte[] _gains = [60, 60, 22, 22, 0];
        public string Description => "MOMENTUM 4 GAIA v3 simulator";
        public List<(ushort Command, byte[] Payload)> Requests { get; } = [];
        public HashSet<ushort> Ignored { get; } = [];
        public byte[] Firmware { get; set; } = [0, 3, 0, 37, 0, 3];
        public byte SoundMode { get; private set; } = 1;
        public List<Host> Hosts { get; } = [new(0, true, "PC-A"), new(1, false, "Laptop-B"), new(2, false, "Phone-C"), new(3, false, "PC-D")];

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            byte[] bytes = data.ToArray();
            ushort command = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6));
            byte[] payload = bytes[8..];
            Requests.Add((command, payload));
            bool apply = !Ignored.Contains(command);
            byte[]? response = command switch
            {
                0x0007 or 0x1A04 or 0x1804 or 0x1A00 or 0x080C => [],
                0x0603 => [90], 0x0602 => [0],
                0x1201 => Firmware, 0x1206 => [.. "M4AEBT Black"u8, 0],
                0x1A05 => [1], 0x1805 => [0], 0x1A01 => [1, 0, 2, 0, 3, 0], 0x080D => [1],
                0x1000 => [5, 0xC4, 0x3C, 0, 0],
                0x100B when payload is [< 5 and var band] => [band, (byte)(Frequencies[band] >> 8), (byte)Frequencies[band]],
                0x1003 when payload.Length == 1 => [.. _gains.Select(g => (byte)g)],
                0x1001 when payload is [< 5 and var band, var gain] => SetGain(apply, band, gain),
                0x0804 => [0, SoundMode],
                0x0803 when payload is [0, <= 2 and var mode] => SetMode(apply, mode),
                0x1400 => [0, (byte)Hosts.Count],
                0x1401 when payload is [var index] && index < Hosts.Count => [index, Hosts[index].Priority, Hosts[index].Connected ? (byte)1 : (byte)0, .. Encoding.UTF8.GetBytes(Hosts[index].Name), 0],
                0x1407 => [0], 0x1409 => [2],
                0x1402 when payload is [var index] && index < Hosts.Count => Connect(apply, index),
                0x1404 when payload is [var index] && index < Hosts.Count => [index, Hosts[index].Connected ? (byte)1 : (byte)0],
                _ => null,
            };
            Emit((ushort)(command | (response is null ? 0x180 : 0x100)), response ?? [1]);
            return ValueTask.CompletedTask;
        }

        private byte[] SetGain(bool apply, byte band, byte gain)
        {
            if (apply) _gains[band] = (sbyte)gain;
            return [];
        }
        private byte[] SetMode(bool apply, byte mode)
        {
            if (apply) SoundMode = mode;
            return [];
        }
        private byte[] Connect(bool apply, byte index)
        {
            if (apply) Hosts[index] = Hosts[index] with { Connected = true };
            return [];
        }

        private void Emit(ushort command, byte[] payload)
        {
            byte[] frame = SennheiserFrameCodec.Encode(command, payload);
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
