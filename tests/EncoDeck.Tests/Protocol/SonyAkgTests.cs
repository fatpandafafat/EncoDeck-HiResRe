// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Akg;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed class SonyAkgTests
{
    [Fact]
    public void Sony_BatteryQueryMatchesApkFraming()
    {
        Assert.Equal(new byte[] { 0x3e, 0x0c, 0, 0, 0, 0, 2, 0x10, 0, 0x1e, 0x3c }, SonyFrameCodec.Encode(12, 0, [0x10, 0]));
        var decoder = new SonyFrameCodec();
        List<SonyFrame> frames = [];
        foreach (byte b in SonyFrameCodec.Encode(12, 1, [0x3c, 0x3d, 0x3e])) frames.AddRange(decoder.Feed([b]));
        Assert.Equal(new byte[] { 0x3c, 0x3d, 0x3e }, Assert.Single(frames).Payload);
        var bad = SonyFrameCodec.Encode(12, 0, [0x11, 0, 80, 0]); bad[^2] ^= 1;
        Assert.Empty(decoder.Feed(bad));
        Assert.Single(decoder.Feed(SonyFrameCodec.Encode(12, 0, [0x11, 0, 80, 0])));
    }

    [Fact]
    public void Akg_FragmentedAndConcatenatedFrames()
    {
        Assert.Equal(new byte[] { 0xaa, 0x21, 1, 0x31 }, AkgFrameCodec.Encode(0x21, [0x31]));
        var decoder = new AkgFrameCodec();
        Assert.Empty(decoder.Feed([0, 0xaa, 0x22]));
        var frames = decoder.Feed([2, 0x31, 1, 0xaa, 0x22, 2, 0x32, 0]);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 0xaa, 0x22, 2, 0x31, 1 }, frames[0]);
    }

    [Theory]
    [InlineData("WH-1000XM4")]
    [InlineData("WH-1000XM5")]
    public async Task Sony_RequiresDeviceReadbackAndUpdatesState(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = new DeviceIdentity("sony-akg-test", name, new BluetoothAddress(0x0011223344AA));
        var profile = (IControllableProfile)SonyAkgProfiles.CreateBuiltIn().Single(p => p.DisplayName == name);
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var channel = new FakeByteChannel();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool reject = false;
        bool v2 = name == "WH-1000XM5";
        var device = Task.Run(async () =>
        {
            int handled = 0; byte seq = 0; byte eq = 0;
            byte[] noise = v2 ? [0x67, 23, 1, 0, 0, 0, 20] : [0x67, 2, 0, 1, 2, 1, 0, 0];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var written = channel.Written;
                    for (; handled < written.Count; handled++)
                    {
                        byte[] request = written[handled]; byte[] response = [];
                        var f = Assert.Single(new SonyFrameCodec().Feed(request));
                        if (f.Type == 1) continue;
                        channel.Receive(SonyFrameCodec.Encode(1, (byte)(f.Sequence ^ 1), []));
                        if (f.Payload[0] == 0) response = v2 ? [1, 0, 0, 0, 2, 0, 0, 0] : [1, 0, 1, 0];
                        if (f.Payload[0] == 4) response = [5, 2, 5, (byte)'1', (byte)'.', (byte)'0', (byte)'.', (byte)'0'];
                        if (f.Payload[0] == 6) response = v2 ? [7, 0, 3, 32, 1, 107, 1, 80, 1] : [7, 0, 3, 0x11, 0x51, 0x62];
                        if (f.Payload[0] == 0x50) response = [0x51, f.Payload[1], 5, 21, 2, 0, 3, 79, 102, 102, 22, 4, 66, 97, 115, 115];
                        if (f.Payload[0] == 0x56) response = [0x57, f.Payload[1], eq, 0];
                        if (f.Payload[0] == 0x58 && !reject) eq = f.Payload[2];
                        if (f.Payload[0] == 0x10) response = [0x11, f.Payload[1], 80, 0];
                        if (f.Payload[0] == 0x22) response = [0x23, f.Payload[1], 80, 0];
                        if (f.Payload[0] == 0x66) response = noise.ToArray();
                        if (f.Payload[0] == 0x68 && !reject) noise = [0x67, .. f.Payload[1..]];
                        if (response.Length > 0) { channel.Receive(SonyFrameCodec.Encode(12, seq, response)); seq ^= 1; }
                    }
                    await Task.Delay(2, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }, ct);
        await using var controller = profile.CreateController(channel, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        try
        {
            Assert.True(await controller.InitializeAsync(ct));
            Assert.Equal(CapabilitySupport.Supported, store.Current.Profile?.Capabilities.Get(DeviceCapability.NoiseCancellation).Support);
            Assert.Equal(80, store.Current.Battery.Left?.Value.Level);
            Assert.Equal("1.0.0", store.Current.Firmware?.Value.Version);
            Assert.Equal(CapabilitySupport.Supported, store.Current.Profile!.Capabilities.Get(DeviceCapability.Equalizer).Support);
            Assert.Equal("Bass", store.Current.Profile.EqualizerPresets.Single(p => p.Id == "sony-22").DisplayName);
            Assert.True((await controller.SetEqualizerPresetAsync("sony-22", ct)).IsSuccess);
            Assert.Equal("sony-22", store.Current.Equalizer!.Value.ActivePresetId);
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("sony-99", ct)).Status);
            Assert.Equal(CommandStatus.Success, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), ct)).Status);
            Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
            Assert.Equal(CommandStatus.Success, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency, SonyAkgProfile.SonyVoice), ct)).Status);
            Assert.Equal(SonyAkgProfile.SonyVoice, store.Current.NoiseControl?.Value.LevelId);
            Assert.Equal(20, store.Current.Options["sony-ambient-level"].Value.Value);
            Assert.True((await controller.SetOptionAsync("sony-ambient-level", 12, ct)).IsSuccess);
            Assert.Equal(12, store.Current.Options["sony-ambient-level"].Value.Value);
            Assert.Equal(SonyAkgProfile.SonyVoice, store.Current.NoiseControl?.Value.LevelId);
            Assert.Equal(CommandStatus.Unsupported, (await controller.SetOptionAsync("sony-ambient-level", 21, ct)).Status);
            reject = true;
            Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("sony-0", ct)).Status);
            Assert.Equal("sony-22", store.Current.Equalizer!.Value.ActivePresetId);
            Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), ct)).Status);
            Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
            Assert.Equal(CommandStatus.Rejected, (await controller.SetOptionAsync("sony-ambient-level", 5, ct)).Status);
        }
        finally { await stop.CancelAsync(); await device; }
    }

    [Fact]
    public void SonyEq_RejectsTruncatedCapabilitiesAndParameters()
    {
        Assert.False(SonyEqualizer.TryPresets([0x51, 0, 5, 21, 1, 0, 3, 79], out _));
        Assert.False(SonyEqualizer.TryPresets([0x51, 0, 5, 21, 2, 0, 0, 0, 0], out _));
        Assert.False(SonyEqualizer.TryParameter([0x57, 0, 22, 5, 1], out _));
        Assert.False(SonyEqualizer.TryParameter([0x57, 3, 22, 0], out _));
        Assert.True(SonyEqualizer.TryParameter([0x57, 4, 22, 2, 10, 10], out var preset));
        Assert.Equal(22, preset);
    }

    [Fact]
    public void SonyEq_ReportsBandsAsDecibelOffsets()
    {
        // Raw band byte 6 is 0 dB; the ten bands run 31 Hz-16 kHz.
        Assert.True(SonyEqualizer.TryParameter([0x57, 0, 0xA1, 10, 9, 8, 6, 5, 6, 7, 8, 9, 8, 7], out byte preset, out int[] bands));
        Assert.Equal(0xA1, preset);
        Assert.Equal([3, 2, 0, -1, 0, 1, 2, 3, 2, 1], bands);
        Assert.True(SonyEqualizer.TryParameter([0x57, 0, 0x30, 0], out _, out int[] none));
        Assert.Empty(none);
        Assert.Equal([0, 12, 6], SonyEqualizer.ToBandBytes([-9, 9, 0]));
    }

    [Fact]
    public void SonyEq_NamesUnnamedKnownPresetsAndKeepsDeviceNames()
    {
        // 0x51 type 0: two unnamed presets (heavy, custom 1), one named by the device, one unknown id.
        byte[] reply = [0x51, 0, 0, 0, 4, 0x30, 0, 0xA1, 0, 0x31, 5, (byte)'B', (byte)'r', (byte)'i', (byte)'t', (byte)'e', 0x44, 0];
        Assert.True(SonyEqualizer.TryPresets(reply, out var presets));
        Assert.Equal(("Heavy", "SonyEq_Heavy", false), (presets[0].DisplayName, presets[0].ResourceKey, presets[0].EditableBands));
        Assert.Equal(("Custom 1", "SonyEq_CustomN:1", true), (presets[1].DisplayName, presets[1].ResourceKey, presets[1].EditableBands));
        Assert.Equal(("Brite", ""), (presets[2].DisplayName, presets[2].ResourceKey));
        Assert.Equal(("EQ 68", ""), (presets[3].DisplayName, presets[3].ResourceKey));
    }

    [Fact]
    public void CatalogOnlyModelsNeverOpenAnUnrelatedControlService()
    {
        var profiles = SonyAkgProfiles.CreateBuiltIn();
        foreach (string name in new[] { "AKG N700NC" })
        {
            var profile = profiles.Single(p => p.DisplayName == name);
            Assert.False(profile is IControllableProfile);
            Assert.Empty(profile.ControlServiceUuids);
        }
        var unknown = new DeviceIdentity("other", "Sony Xperia", new BluetoothAddress(1));
        Assert.All(profiles, p => Assert.Equal(0, p.MatchScore(unknown)));
    }

    [Fact]
    public async Task SilentSonyServiceCannotInitializeSuccessfully()
    {
        var identity = new DeviceIdentity("silent", "WH-1000XM5", new BluetoothAddress(1));
        var profile = (IControllableProfile)SonyAkgProfiles.CreateBuiltIn().Single(p => p.DisplayName == identity.Name);
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var channel = new FakeByteChannel();
        await using var controller = profile.CreateController(channel, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.False(await controller.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.True(controller.Closed.IsCompleted);
        Assert.Equal(11, channel.Written.Count);
        Assert.Null(store.Current.NoiseControl);
        Assert.Null(store.Current.Battery.Left);
    }

    [Theory]
    [InlineData(19, 7)]
    [InlineData(20, 7)]
    [InlineData(21, 8)]
    [InlineData(22, 8)]
    [InlineData(23, 7)]
    [InlineData(24, 7)]
    [InlineData(25, 9)]
    public void SonyV2_ApkLayoutsPreserveUnrelatedParameters(int type, int length)
    {
        byte[] original = new byte[length];
        original[0] = 0x67; original[1] = (byte)type; original[2] = 1; original[3] = 1;
        if (length == 9) original[8] = 2;
        foreach (var target in new[] { NoiseControlMode.Off, NoiseControlMode.NoiseCancellation, NoiseControlMode.Transparency })
        {
            var payload = SonyV2Noise.SetPayload(original, target);
            Assert.NotNull(payload);
            Assert.True(SonyV2Noise.TryRead([0x67, .. payload], out var actual));
            Assert.Equal(target, actual);
            if (length == 9) Assert.Equal(2, payload[7]);
        }
        Assert.Null(SonyV2Noise.SetPayload([0x67, 0xff, 1, 1, 0, 0, 0], NoiseControlMode.Off));
    }

    [Theory]
    [InlineData("AKG N400NC", false)]
    [InlineData("AKG Y600", true)]
    public async Task AkgBes_ReadsDeviceInfoAndConfirmsWritesByStatusReadback(string name, bool autoPlay)
    {
        var ct = TestContext.Current.CancellationToken;
        var identity = new DeviceIdentity("akg-bes", name, new BluetoothAddress(1));
        var profile = (IControllableProfile)SonyAkgProfiles.CreateBuiltIn().Single(p => p.DisplayName == name);
        Assert.Equal([AkgBleUuids.BesService, AkgBleUuids.Spp], profile.ControlServiceUuids);
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var device = new BesPeer { AutoPlaySupported = autoPlay };
        await using var controller = profile.CreateController(device, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(ct));

        // Device-info byte 22 = 0x80 | 55: 55 %, charging (the battery opcode 0x25 is never sent).
        Assert.Equal(new BatteryReading(55, true), store.Current.Battery.Left?.Value);
        Assert.Equal("1.2.3", store.Current.Firmware?.Value.Version);
        Assert.DoesNotContain(device.Commands, c => c == 0x25);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
        Assert.Equal("akg-eq-0", store.Current.Equalizer?.Value.ActivePresetId);
        var current = Assert.IsAssignableFrom<IControllableProfile>(store.Current.Profile);
        Assert.Equal(9, current.EqualizerPresets.Count);
        Assert.Equal(autoPlay, current.Toggles.Contains(DeviceToggle.InEarDetection));

        // Ambient modes are exclusive with ANC: ANC goes off before talk-through is enabled.
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency, SonyAkgProfile.AkgTalkThrough), ct)).IsSuccess);
        Assert.Equal((0, 1), (device.Anc, device.Ambient));
        Assert.Equal(SonyAkgProfile.AkgTalkThrough, store.Current.NoiseControl?.Value.LevelId);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), ct)).IsSuccess);
        Assert.Equal((0, 0), (device.Anc, device.Ambient));
        Assert.True((await controller.SetEqualizerPresetAsync("akg-eq-3", ct)).IsSuccess);
        Assert.Equal("akg-eq-3", store.Current.Equalizer?.Value.ActivePresetId);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("akg-eq-9", ct)).Status);
        var toggle = await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, ct);
        Assert.Equal(autoPlay ? CommandStatus.Success : CommandStatus.Unsupported, toggle.Status);

        device.Reject = true;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), ct)).Status);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl?.Value.Mode);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("akg-eq-5", ct)).Status);
        Assert.Equal("akg-eq-3", store.Current.Equalizer?.Value.ActivePresetId);
    }

    /// <summary>BES <c>AA</c> peer following the N400NC behaviour recorded by tthayer/akg_earbud_control.</summary>
    private sealed class BesPeer : IDuplexByteChannel
    {
        private readonly System.Threading.Channels.Channel<byte[]> _responses = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public bool AutoPlaySupported { get; init; }
        public bool Reject { get; set; }
        public int Anc { get; private set; } = 1;
        public int Ambient { get; private set; }
        public int Eq { get; private set; }
        public int AutoPlay { get; private set; } = 1;
        public List<byte> Commands { get; } = [];
        public string Description => "Simulated AKG BES device";

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            byte[] f = data.ToArray();
            Assert.Equal(0xAA, f[0]);
            Assert.Equal(f.Length - 3, f[2]);
            Commands.Add(f[1]);
            switch (f[1])
            {
                case 0x11:
                    byte[] info = new byte[31];
                    "AKG N400NC"u8.CopyTo(info);
                    info[16] = 0x3A; info[17] = 0x1F; info[19] = 0x80 | 55; info[26] = 1; info[27] = 2; info[28] = 3;
                    Send(AkgFrameCodec.Encode(0x12, info));
                    break;
                case 0x21 when f[3] == 0x30: Send(AkgFrameCodec.Encode(0x22, [0x30, (byte)Anc, (byte)Ambient, 0x85, (byte)Eq])); break;
                case 0x21 when f[3] == 0x38 && AutoPlaySupported: Send(AkgFrameCodec.Encode(0x22, [0x38, (byte)AutoPlay])); break;
                case 0x31 when !Reject: Anc = f[3]; break;
                case 0x32 when !Reject: Ambient = f[3]; break;
                case 0x40 when !Reject: Eq = f[3]; break;
                case 0x35 when !Reject && AutoPlaySupported: AutoPlay = f[3]; break;
            }
            return ValueTask.CompletedTask;
        }

        // Split each reply to exercise reassembly across notifications.
        private void Send(byte[] frame) { _responses.Writer.TryWrite(frame[..2]); _responses.Writer.TryWrite(frame[2..]); }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try { var response = await _responses.Reader.ReadAsync(cancellationToken); response.CopyTo(buffer); return response.Length; }
            catch (System.Threading.Channels.ChannelClosedException) { return 0; }
        }

        public ValueTask DisposeAsync() { _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
