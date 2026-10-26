// SPDX-License-Identifier: GPL-3.0-only
// Vectors ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6,
// GPL-3.0-or-later: pbpctrl hash/address facts, the public PixelBudsMacOS captured settings notification and
// MagicBuds battery semantics, as attributed there. No device identifiers or private captures.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Google;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public class GoogleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(Uri.IsHexDigit)));

    private const uint GetSoftwareInfo = 0x7199FA44, ReadSetting = 0xAED0AE51, WriteSetting = 0x9E8C9A1D,
        SubscribeRuntimeInfo = 0xE61E8290, SubscribeToSettingsChanges = 0x2821ADF5;

    // PixelBudsMacOS CapturedFramesTests frame 5: a settings notification only.
    private static readonly byte[] Notification = Hex("""
        7E 00 A5 03 2A 04 22 02 68 03 08 07 10 15 1D EA
        71 DE 7D 5E 25 F5 AD 21 28 38 09 08 E4 5A D2 7E
        """);

    [Fact]
    public void GoldenHashesRequestAndCapturedNotification_SurviveEveryStreamSplit()
    {
        Assert.Equal(0x7EDE71EAu, GoogleMaestro.ServiceId);
        Assert.Equal(GetSoftwareInfo, GoogleMaestro.Hash("GetSoftwareInfo"));
        Assert.Equal(SubscribeToSettingsChanges, GoogleMaestro.Hash("SubscribeToSettingsChanges"));
        Assert.Equal(ReadSetting, GoogleMaestro.Hash("ReadSetting"));
        Assert.Equal(0xCBF43926u, GoogleFrameCodec.Crc32("123456789"u8));
        var request = new GoogleRpcFrame(19, ReadSetting, 1, [0x20, 0x0D]);
        Assert.Equal(Hex("7E 00 3B 03 08 00 10 13 1D EA 71 DE 7D 5E 25 51 AE D0 AE 2A 02 20 0D 30 00 38 01 18 78 9A 6F 7E"), GoogleFrameCodec.Encode(request));
        for (int split = 0; split <= Notification.Length; split++)
        {
            var codec = new GoogleFrameCodec();
            var frames = codec.Feed(Notification.AsSpan(0, split)).Concat(codec.Feed(Notification.AsSpan(split))).ToList();
            var frame = Assert.Single(frames);
            Assert.Equal((7u, 21u, 9u), (frame.Type, frame.Channel, frame.Call));
            Assert.Equal((0x7EDE71EAu, SubscribeToSettingsChanges), (frame.Service, frame.Method));
            Assert.Equal(new byte[] { 0x22, 2, 0x68, 3 }, frame.Payload);
            Assert.Equal(NoiseControlMode.Transparency, GoogleMaestro.Noise(frame.Payload));
        }
        Assert.Equal(new uint[] { 19, 24, 21, 26 }, GoogleMaestro.Candidates);
        foreach (var (channel, address) in new (uint, uint)[] { (19, 3712), (21, 4736), (24, 3904), (26, 4928) })
            Assert.Equal(address, GoogleFrameCodec.Address(channel));
        Assert.Throws<ArgumentException>(() => GoogleFrameCodec.Encode(new GoogleRpcFrame(999, 0, 1)));
    }

    [Fact]
    public void Decoder_DropsCorruptionTruncationInvalidEscapeAndOversizedFrames()
    {
        byte[] badCrc = [.. Notification];
        badCrc[^2] ^= 1;
        foreach (byte[] bytes in new[] { badCrc, [0x7E, 0x7D, 0, 0x7E], [0x7E, 0x7D, 0x7E] })
        {
            var codec = new GoogleFrameCodec();
            Assert.Empty(codec.Feed(bytes));
            Assert.Single(codec.Feed(Notification));
        }
        for (int split = 1; split < Notification.Length; split++)
        {
            var codec = new GoogleFrameCodec();
            Assert.Empty(codec.Feed(Notification.AsSpan(0, split)));
            codec.Reset();
            Assert.Single(codec.Feed(Notification));
        }
        var oversized = new GoogleFrameCodec();
        Assert.Empty(oversized.Feed([0x7E, .. new byte[16_385]]));
        Assert.Single(oversized.Feed(Notification));
    }

    [Fact]
    public void Protobuf_BoundsWrongWireTypesAndDuplicateSingularFields()
    {
        byte[][] bad =
        [
            [0], [0x0B], [0x0F], [0x08, 0x80], [0x0A, 3, 1], [0x0D, 0, 0, 0], [0x09, 0, 0, 0, 0, 0, 0, 0],
            [0x08, .. Enumerable.Repeat((byte)0xFF, 9), 2], new byte[16_385],
        ];
        Assert.NotNull(ProtoFields.TryParse([]));
        foreach (var bytes in bad) Assert.Null(ProtoFields.TryParse(bytes));
        var duplicate = ProtoFields.TryParse([8, 1, 8, 2])!;
        Assert.True(duplicate.Present(1));
        Assert.Null(duplicate.One(1));
        Assert.Null(duplicate.UInt(1));
        Assert.Null(ProtoFields.TryParse([.. Enumerable.Repeat(new byte[] { 8, 0 }, 257).SelectMany(b => b)]));
        Assert.Equal(ulong.MaxValue, ProtoFields.TryParse([8, .. Enumerable.Repeat((byte)0xFF, 9), 1])!.UInt(1));

        byte[] header = Hex("10 13 1D EA 71 DE 7E 25 51 AE D0 AE 38 01");
        Assert.Empty(GoogleRpcFrame.Decode(header)!.Payload);
        byte[][] suffixes =
        [
            [8, 9], [8, 1, 8, 1], [0x2A, 0, 0x2A, 0], [0x30, 0, 0x30, 0], [0x0D, 0, 0, 0, 0], [0x28, 0],
            [0x10, 0x13], [0x38, 1], [0x30, 0x80, 0x80, 0x80, 0x80, 0x10],
        ];
        foreach (var suffix in suffixes) Assert.Null(GoogleRpcFrame.Decode([.. header, .. suffix]));
        Assert.Null(GoogleRpcFrame.Decode([0x10, 0x13]));
    }

    [Fact]
    public void Settings_RequireExactPresenceAndFiniteEqBands()
    {
        // MagicBuds notes confirm 22 02 68 03 on a real Pro 2.
        Assert.Equal(new byte[] { 0x22, 2, 0x68, 3 }, GoogleMaestro.NoiseWrite(NoiseControlMode.Transparency));
        foreach (byte[] bytes in new byte[][] { [], [0x22, 0], [0x22, 2, 0x68, 0], [0x22, 2, 0x68, 5], [0x22, 4, 0x68, 1, 0x68, 2], [0x22, 3, 0x6A, 1, 1] })
            Assert.Null(GoogleMaestro.Noise(bytes));
        // An explicitly present empty band message is a flat curve.
        Assert.Equal(new float[5], GoogleMaestro.Equalizer([0x22, 3, 0x82, 1, 0]));
        Assert.Null(GoogleMaestro.Equalizer([]));
        Assert.Null(GoogleMaestro.Equalizer([0x22, 0]));
        Assert.Equal(new float[] { 5, 0, 0, 0, 0 }, GoogleMaestro.Equalizer(Hex("22 08 82 01 05 0D 00 00 A0 40")));
        foreach (byte[] field in new byte[][] { [8, 0], [0x0D, 0, 0, 0xC0, 0x7F], [0x0D, 0, 0, 0x80, 0x7F], [0x0D, 0, 0, 0xE0, 0x40], [0x0D, 0, 0, 0, 0, 0x0D, 0, 0, 0, 0] })
        {
            byte[] eq = ProtoFields.Encode(4, new ProtoValue.Bytes(ProtoFields.Encode(16, new ProtoValue.Bytes(field))));
            Assert.Null(GoogleMaestro.Equalizer(eq));
        }
        foreach (float[] bands in new float[][] { [0, 0], [-7, 0, 0, 0, 0], [float.NaN, 0, 0, 0, 0], [float.PositiveInfinity, 0, 0, 0, 0] })
            Assert.Throws<ArgumentException>(() => GoogleMaestro.EqualizerWrite(bands));
        for (int i = 0; i < GoogleMaestro.Presets.Length; i++)
            Assert.Equal(i, GoogleMaestro.Preset(GoogleMaestro.Equalizer(GoogleMaestro.EqualizerWrite(GoogleMaestro.Presets[i]))!));
        Assert.Null(GoogleMaestro.Preset([1, 1, 1, 1, 1]));
    }

    [Fact]
    public void RuntimeBattery_DistinguishesZeroUnknownMalformedAndAbsentComponents()
    {
        var full = GoogleMaestro.Runtime(Hex("32 10 0A 04 08 50 10 02 12 02 10 01 1A 04 08 64 10 01"))!.Value;
        Assert.Equal(new BatteryReading(80, true), full.Case);
        Assert.Equal(new BatteryReading(0, false), full.Left);
        Assert.Equal(new BatteryReading(100, false), full.Right);
        Assert.Null(GoogleMaestro.Runtime(Hex("32 09 12 04 08 01 10 01 1A 01 08")));
        var unknown = GoogleMaestro.Runtime(Hex("32 0C 12 04 08 01 10 00 1A 04 08 65 10 01"))!.Value;
        Assert.Null(unknown.Left);
        Assert.Null(unknown.Right);
        Assert.Null(GoogleMaestro.Runtime([]));
        // The case is reported only while a bud sits in it; an omitted case is unknown.
        var budOnly = GoogleMaestro.Runtime(Hex("32 06 12 04 08 28 10 01"))!.Value;
        Assert.Null(budOnly.Case);
        Assert.Equal(40, budOnly.Left!.Level);
    }

    [Theory]
    [InlineData("Pixel Buds Pro", "google-pixel-buds-pro")]
    [InlineData("Google Pixel Buds Pro", "google-pixel-buds-pro")]
    [InlineData("Google Pixel Buds Pro 2", "google-pixel-buds-pro-2")]
    [InlineData("Pixel Buds Pro 3", null)]
    [InlineData("Pixel Buds A-Series", null)]
    [InlineData("Pixel Buds Pro 2 Case", null)]
    public void Names_MatchOnlyTheTwoMaestroModels(string name, string? expected)
    {
        var profile = new DeviceProfileRegistry(BuiltInProfiles.Create()).Match(new DeviceIdentity("dev", name, new BluetoothAddress(0x0011223344AA)));
        Assert.Equal(expected, profile?.ModelId);
        if (expected is null) return;
        var google = Assert.IsType<GoogleProfile>(profile);
        Assert.Equal("google", google.Brand);
        Assert.Equal(new[] { GoogleMaestro.ServiceUuid }, google.ControlServiceUuids);
        var key = new DeviceResourceKey(expected, null, null, name, google.DisplayName);
        Assert.False(key.UsesOplusId);
        Assert.False(key.CanDownload);
    }

    private sealed class Device : IDuplexByteChannel
    {
        private readonly FakeByteChannel _inner = new();
        public string Description => "Maestro fixture";
        public List<GoogleRpcFrame> Commands { get; } = [];
        public Func<GoogleRpcFrame, GoogleRpcFrame[]?>? Override;
        public byte Noise = 2;
        public byte[] Eq = [0x22, 3, 0x82, 1, 0];
        public HashSet<uint> RejectedSettingRoutes = [];
        public bool ApplyWrites = true;

        public static GoogleRpcFrame Response(GoogleRpcFrame request, uint type = 1, uint status = 0, byte[]? payload = null) =>
            new(request.Channel, request.Method, request.Call, payload, type, status);

        public void Push(GoogleRpcFrame frame) => _inner.Receive(GoogleFrameCodec.Encode(frame));

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _inner.WriteAsync(data, ct);
            foreach (var request in new GoogleFrameCodec().Feed(data.Span))
            {
                Commands.Add(request);
                foreach (var reply in Override?.Invoke(request) ?? Default(request)) Push(reply);
            }
        }

        private GoogleRpcFrame[] Default(GoogleRpcFrame request)
        {
            switch (request.Method)
            {
                case GetSoftwareInfo:
                    return [Response(request, payload: [0x22, 7, 0x12, 5, 0x12, 3, 0x31, 0x2E, 0x30])];
                case ReadSetting:
                    if (RejectedSettingRoutes.Contains(request.Channel)) return [Response(request, status: 2)];
                    return [Response(request, payload: request.Payload is [0x20, 13] ? [0x22, 2, 0x68, Noise] : Eq)];
                case WriteSetting:
                    if (ApplyWrites && GoogleMaestro.Noise(request.Payload) is { } mode)
                        Noise = mode switch { NoiseControlMode.Off => 1, NoiseControlMode.NoiseCancellation => 2, _ => 3 };
                    else if (ApplyWrites && GoogleMaestro.Equalizer(request.Payload) is not null)
                        Eq = request.Payload;
                    return [Response(request)];
                case SubscribeRuntimeInfo:
                    return [Response(request, type: 7, payload: [0x32, 6, 0x12, 4, 8, 70, 0x10, 1])];
                case SubscribeToSettingsChanges:
                    return [];
                default:
                    return [Response(request, status: 12)];
            }
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static (GoogleDeviceController Controller, DeviceStateStore Store) Create(Device device, string name = "Pixel Buds Pro", int timeoutMs = 3000)
    {
        var profile = (GoogleProfile)GoogleProfiles.CreateBuiltIn().Single(p => p.DisplayName == name);
        var identity = new DeviceIdentity("google-test", name, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (new GoogleDeviceController(device, context, profile, TimeSpan.FromMilliseconds(timeoutMs)), store);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10, Ct);
        Assert.True(condition());
    }

    [Theory]
    [InlineData("Pixel Buds Pro")]
    [InlineData("Pixel Buds Pro 2")]
    public async Task BothModels_ProbeReadOnlyAndConfirmSettingsByReadback(string name)
    {
        var device = new Device();
        var (controller, store) = Create(device, name);
        await using var session = controller;
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
        Assert.Equal(CapabilitySupport.Unknown, store.Current.Profile.Capabilities.Get(DeviceCapability.Equalizer).Support);
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal("1.0", store.Current.Firmware!.Value.Version);
        Assert.All(device.Commands.TakeWhile(c => c.Method != SubscribeRuntimeInfo), c => Assert.Contains(c.Method, new[] { GetSoftwareInfo, ReadSetting }));
        Assert.Equal(3, store.Current.Profile!.NoiseControlOptions.Count);
        Assert.Equal(5, store.Current.Profile.EqualizerPresets.Count);
        Assert.Equal(70, store.Current.Battery.Left!.Value.Level);
        Assert.Null(store.Current.Battery.Case);

        int count = device.Commands.Count;
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(WriteSetting, device.Commands[count].Method);
        Assert.Equal(new byte[] { 0x22, 2, 0x68, 3 }, device.Commands[count].Payload);
        Assert.Equal(ReadSetting, device.Commands[count + 1].Method);
        Assert.Equal(new byte[] { 0x20, 13 }, device.Commands[count + 1].Payload);
        Assert.True((await controller.SetEqualizerPresetAsync("1", Ct)).IsSuccess);
        Assert.Equal("1", store.Current.Equalizer!.Value.ActivePresetId);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl!.Value.Mode);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Adaptive), Ct)).Status);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetEqualizerPresetAsync("5", Ct)).Status);
    }

    [Fact]
    public async Task SoftwareInfoOnlyRoute_DoesNotHideReadableAlternative()
    {
        var device = new Device { RejectedSettingRoutes = [19] };
        var (controller, store) = Create(device, "Pixel Buds Pro 2");
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.NotEmpty(store.Current.Profile!.NoiseControlOptions);
        Assert.Contains(device.Commands, c => c.Channel != 19 && c.Method == ReadSetting);
        Assert.DoesNotContain(device.Commands, c => c.Method == WriteSetting);
    }

    [Fact]
    public async Task UnappliedWrite_IsRejectedAfterReadback()
    {
        var device = new Device { ApplyWrites = false };
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("2", Ct)).Status);
        Assert.Equal("0", store.Current.Equalizer!.Value.ActivePresetId);
    }

    [Fact]
    public async Task MalformedReadback_CannotConfirmCachedNoiseOrFlatEq()
    {
        var device = new Device();
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
        Assert.Equal("0", store.Current.Equalizer!.Value.ActivePresetId);
        foreach (byte[] invalid in new byte[][] { [], [0x22, 0], [0x22, 1, 0x68] })
        {
            device.Override = null;
            Assert.True((await controller.RefreshAsync(Ct)).IsSuccess);
            Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl!.Value.Mode);
            Assert.Equal("0", store.Current.Equalizer!.Value.ActivePresetId);
            device.Override = r => r.Method == ReadSetting ? [Device.Response(r, payload: invalid)] : null;
            Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).Status);
            Assert.Equal(CommandStatus.Rejected, (await controller.SetEqualizerPresetAsync("0", Ct)).Status);
        }
        Assert.Null(store.Current.NoiseControl);
        Assert.Null(store.Current.Equalizer);
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
        Assert.Empty(store.Current.Profile.EqualizerPresets);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
    }

    [Fact]
    public async Task WrongRpcIdentityAndNonzeroStatus_CannotCompleteCall()
    {
        var device = new Device();
        var (controller, _) = Create(device, timeoutMs: 300);
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        device.Override = r => r.Method != WriteSetting ? null :
        [
            new GoogleRpcFrame(1, r.Channel, 1, r.Method, r.Call, 0, []),
            Device.Response(r with { Call = r.Call + 1 }),
            Device.Response(r with { Channel = r.Channel == 19 ? 21u : 19u }),
            Device.Response(r with { Method = r.Method + 1 }),
            Device.Response(r, type: 7),
            Device.Response(r, status: 7),
        ];
        int count = device.Commands.Count;
        Assert.Equal(CommandStatus.Rejected, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.Equal(count + 1, device.Commands.Count); // explicit server error: no read-back
        device.Override = r => r.Method == WriteSetting ? [Device.Response(r with { Call = r.Call + 1 })] : null;
        Assert.Equal(CommandStatus.Timeout, (await controller.SetNoiseControlAsync(new(NoiseControlMode.Off), Ct)).Status);
        Assert.True(controller.Closed.IsCompleted);
    }

    [Fact]
    public async Task SubscriptionCallbacks_AreScopedToTheirCall()
    {
        var device = new Device();
        var (controller, store) = Create(device, "Pixel Buds Pro 2");
        await using var session = controller;
        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(70, store.Current.Battery.Left!.Value.Level);
        var stream = device.Commands.First(c => c.Method == SubscribeToSettingsChanges);
        device.Push(Device.Response(stream with { Call = stream.Call + 100 }, type: 7, payload: [0x22, 2, 0x68, 1]));
        device.Push(Device.Response(stream, type: 7, payload: [0x22, 2, 0x68, 3]));
        await Until(() => store.Current.NoiseControl?.Value.Mode == NoiseControlMode.Transparency);
        // A settings stream may also carry the EQ curve; a non-preset curve keeps no preset selected.
        device.Push(Device.Response(stream, type: 7, payload: Hex("22 08 82 01 05 0D 00 00 A0 40")));
        await Until(() => store.Current.Equalizer?.Value.ActivePresetId is null && store.Current.Equalizer is not null);
        // A server error ends the stream; later packets for that call are ignored.
        device.Push(Device.Response(stream, type: 5, status: 1));
        device.Push(Device.Response(stream, type: 7, payload: [0x22, 2, 0x68, 1]));
        await Task.Delay(100, Ct);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl!.Value.Mode);
    }

    [Fact]
    public async Task NoAnsweringRoute_DoesNotEstablishSession()
    {
        var device = new Device { Override = r => [Device.Response(r, status: 5)] };
        var (controller, store) = Create(device);
        await using var session = controller;
        Assert.False(await controller.InitializeAsync(Ct));
        Assert.Empty(store.Current.Profile!.NoiseControlOptions);
        Assert.Equal(4, device.Commands.Count(c => c.Method == GetSoftwareInfo));
        Assert.DoesNotContain(device.Commands, c => c.Method == WriteSetting);
    }
}
