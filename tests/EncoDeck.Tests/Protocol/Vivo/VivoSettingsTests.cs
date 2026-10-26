// SPDX-License-Identifier: GPL-3.0-only
// Fixtures: TWS 3e fw 2.5.9 frames from TWS-Pods-PC vivo/test_vivo.py and PROTOCOL.md
// (https://github.com/Zhaoyi-ya/TWS-Pods-PC, 3350f42, GPL-3.0-only); TWS 5 fw 2.11.1 dual connection
// list (sanitized) from vivopods-mac Sources/VivoSelfTest/MultiConnectChecks.swift
// (https://github.com/FxRayHughes/vivopods-mac, be25133, GPL-3.0-only); TWS Air3 Pro battery from
// HyperEars docs/vivo-tws-air3-pro-protocol.md (https://github.com/silverpoetry/HyperEars, 361d279, GPL-3.0-only).
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Vivo;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Vivo;

public sealed class VivoSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    private static VivoFrame Decode(string wire) => Assert.Single(new VivoFrameDecoder().Feed(Hex(wire)));

    /// <summary>vivopods-mac's TWS 5 capture: a MacBook (playing), a phone and a tablet; names sanitized, byte lengths kept.</summary>
    private const string Tws5DeviceList =
        "00 1B 82 49 00 03 " +
        "AA BB CC 44 55 66 38 01 0C 02 17 " +
        "E5 BC A0 E5 B0 8F E6 98 8E E7 9A 84 4D 61 63 42 6F 6F 6B 20 41 69 72 " +
        "DD EE FF 77 88 99 5A 02 0C 00 10 " +
        "E5 B0 8F E6 98 8E E7 9A 84 69 51 4F 4F 20 31 33 " +
        "11 22 33 AA BB CC 5A 41 1C 00 0D " +
        "69 51 4F 4F 20 50 61 64 32 20 50 72 6F";

    // ---- codec --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x0150, "03 02", "FF 03 00 02 00 1B 01 50 03 02")]
    [InlineData(0x0131, "05 08 09", "FF 03 00 03 00 1B 01 31 05 08 09")]
    [InlineData(0x0231, "05", "FF 03 00 01 00 1B 02 31 05")]
    [InlineData(0x0120, "01", "FF 03 00 01 00 1B 01 20 01")]
    [InlineData(0x0118, "05", "FF 03 00 01 00 1B 01 18 05")]
    public void Encode_MatchesTws3eCaptures(int command, string payload, string wire) =>
        Assert.Equal(Hex(wire), new VivoFrame((ushort)command, Hex(payload)).Encode(3));

    [Fact]
    public void Tws3eReports_DecodeGestureAndWearSwitchFrames()
    {
        var tap = Decode("FF 03 00 03 00 1B 82 02 00 01 11");
        Assert.True(VivoParsers.TryParseDoubleTap(tap.Payload, out byte left, out byte right));
        Assert.Equal(1, VivoGestures.DoubleTapFunction(EarSide.Left, left));
        Assert.Equal(1, VivoGestures.DoubleTapFunction(EarSide.Right, right));
        // A code from the other ear's range is kept, not folded into a known action.
        Assert.Equal(0x101, VivoGestures.DoubleTapFunction(EarSide.Right, 0x01));

        Assert.True(VivoParsers.TryParseLongPress(Decode("FF 03 00 03 00 1B 82 50 00 03 02").Payload, out byte hold));
        Assert.Equal(VivoGestures.LongPressNoiseControl, hold);

        Assert.True(VivoParsers.TryParseNoiseCycle(Decode("FF 03 00 04 00 1B 82 31 00 05 0B 0A").Payload, out byte cycleL, out byte cycleR));
        Assert.Equal((0x0B, 0x0A), (cycleL, cycleR));

        Assert.True(VivoParsers.TryParseWearDetection(Decode("FF 03 00 02 00 1B 82 03 00 01").Payload, out bool on));
        Assert.True(on);
        Assert.True(VivoParsers.TryParseWearDetection(Decode("FF 03 00 02 00 1B 82 03 00 00").Payload, out bool off));
        Assert.False(off);
        // The bare registration answer is not a switch report.
        Assert.False(VivoParsers.TryParseWearDetection([0], out _));
        Assert.False(VivoParsers.TryParseWearDetection([0, 2], out _));
        Assert.False(VivoParsers.TryParseDoubleTap([0], out _, out _));
    }

    [Theory]
    [InlineData("FF 03 00 05 00 1B 82 07 00 5C 59 48 05", 92, 89, 72, true, false, true)]
    [InlineData("FF 03 00 05 00 1B 82 07 00 53 52 5F 00", 83, 82, 95, false, false, false)]
    public void Battery_DecodesCommunityCapturesWithChargingBits(string wire, int l, int r, int c, bool lc, bool rc, bool cc)
    {
        Assert.True(VivoParsers.TryParseBattery(Decode(wire).Payload, out var left, out var right, out var @case));
        Assert.Equal(new BatteryReading(l, lc), left);
        Assert.Equal(new BatteryReading(r, rc), right);
        Assert.Equal(new BatteryReading(c, cc), @case);
    }

    [Fact]
    public void SourceDevices_DecodeTheTws5CaptureWithUtf8Names()
    {
        var frame = new VivoFrame(0x8249, Hex(Tws5DeviceList)[4..]);
        Assert.Equal(87, frame.Payload.Length);
        Assert.True(VivoParsers.TryParseSourceDevices(frame.Payload, out var devices));
        Assert.Equal(3, devices.Count);
        Assert.Equal(Hex("AA BB CC 44 55 66"), devices[0].Address);
        Assert.Equal("张小明的MacBook Air", devices[0].Name);
        Assert.Equal(VivoConstants.SourceActive, devices[0].State);
        Assert.Equal("小明的iQOO 13", devices[1].Name);
        Assert.Equal(VivoConstants.SourceDisconnected, devices[1].State);
        Assert.Equal("iQOO Pad2 Pro", devices[2].Name);
        Assert.Equal([2, 1, 3], devices.Select(d => VivoParsers.DeviceType(d.DeviceClass)));

        // A truncated entry rejects the report instead of showing a partial list.
        Assert.False(VivoParsers.TryParseSourceDevices(frame.Payload[..^1], out _));
        Assert.False(VivoParsers.TryParseSourceDevices([1, 0], out _));
    }

    [Theory]
    [InlineData("Bluetooth#Bluetoothaa:bb:cc:44:55:66-11:22:33:44:a1:b2", "AABBCC445566")]
    [InlineData("BluetoothLE#BluetoothLE8c:f8:c5:11:22:33-e4:98:e0:3a:2a:a3", "8CF8C5112233")]
    [InlineData("dev-vivo", null)]
    [InlineData("Bluetooth#Bluetoothaa:bb:cc:44:55-11", null)]
    public void HostAddress_ComesFromTheAssociationEndpointId(string deviceId, string? expected)
    {
        bool parsed = VivoParsers.TryParseHostAddress(deviceId, out var address);
        Assert.Equal(expected is not null, parsed);
        if (expected is not null) Assert.Equal(Convert.FromHexString(expected), address);
    }

    [Fact]
    public void Catalog_MarksDualConnectionOnlyForDeclaredModels()
    {
        Assert.True(VivoCatalog.All.Single(m => m.ModelId == "vivo-tws-5").DualConnection);
        Assert.True(VivoCatalog.All.Single(m => m.ModelId == "iqoo-headphones").DualConnection);
        Assert.False(VivoCatalog.All.Single(m => m.ModelId == "vivo-tws-3e").DualConnection);
        Assert.False(VivoCatalog.All.Single(m => m.ModelId == "vivo-tws-4").DualConnection);
        Assert.Equal(11, VivoCatalog.All.Count(m => m.DualConnection));
        // "vivo TWS Air200" is a reported Bluetooth name of the Air2.
        var registry = new DeviceProfileRegistry(VivoProfiles.CreateBuiltIn());
        Assert.Equal("vivo-tws-air2", registry.Match(new DeviceIdentity("id", "vivo TWS Air200", new BluetoothAddress(1)))?.ModelId);
    }

    // ---- simulated earbuds ----------------------------------------------------------------------

    [Fact]
    public async Task Tws3e_OffersGesturesAndWearSwitchAfterReadBack_AndConfirmsWrites()
    {
        await using var device = new FakeVivoEarbuds();
        var (controller, store) = Create(device, "vivo TWS 3e");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var profile = Assert.IsType<VivoDeviceProfile>(store.Current.Profile);
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.TouchCustomization).Support);
        Assert.Contains(DeviceToggle.InEarDetection, profile.Toggles);
        Assert.True(store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        Assert.Equal(5, store.Current.KeyFunctions!.Value.Count);
        Assert.Equal(5, profile.GestureSlots.Count);
        Assert.Contains(new KeyFunction(EarSide.Left, VivoGestures.CycleButton, GestureAction.SqueezeAndHold, 0x0B), store.Current.KeyFunctions.Value);

        // Left double-tap → next track (code 0x03), right one unchanged.
        Assert.True((await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, 0x03), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x03 }, device.Last(VivoCommands.SetDoubleTap).Payload);
        Assert.Contains(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, 0x03), store.Current.KeyFunctions!.Value);
        Assert.Empty(store.Current.PendingCommands);

        // Right double-tap uses the 0x10 range.
        Assert.True((await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Right, 0, GestureAction.DoubleSqueeze, 0x06), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x16 }, device.Last(VivoCommands.SetDoubleTap).Payload);

        // The right cycle write repeats the left earbud's reported byte.
        Assert.True((await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Right, VivoGestures.CycleButton, GestureAction.SqueezeAndHold, 0x09), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x05, 0x0B, 0x09 }, device.Last(VivoCommands.SetNoiseCycle).Payload);

        Assert.True((await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Both, 0, GestureAction.SqueezeAndHold, VivoGestures.LongPressDeclineCall), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x03, 0x03 }, device.Last(VivoCommands.SetLongPress).Payload);

        // Unknown codes are never written.
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, 0x07), Ct)).Status);
        // With long press no longer on noise control the cycle cannot be written.
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, VivoGestures.CycleButton, GestureAction.SqueezeAndHold, 0x08), Ct)).Status);

        Assert.True((await controller.SetToggleAsync(DeviceToggle.InEarDetection, false, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x00 }, device.Last(VivoCommands.SetWearDetection).Payload);
        Assert.False(store.Current.Toggles[DeviceToggle.InEarDetection].Value);
        Assert.All(device.Received.Where(f => f.Command is VivoCommands.SetDoubleTap or VivoCommands.SetLongPress or VivoCommands.SetNoiseCycle or VivoCommands.SetWearDetection),
            f => Assert.Equal(3, device.VersionOf(f)));
    }

    [Fact]
    public async Task Tws3e_ReportsTheEarlierValueWhenTheEarbudsIgnoreAWrite()
    {
        await using var device = new FakeVivoEarbuds { IgnoreWrites = true };
        var (controller, store) = Create(device, "vivo TWS 3e");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var result = await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, 0x02), Ct);
        Assert.Equal(CommandStatus.Rejected, result.Status);
        Assert.Contains(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, 0x01), store.Current.KeyFunctions!.Value);
        Assert.NotNull(store.Current.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherModels_KeepUntestedGesturesExperimental(bool allowExperimental)
    {
        await using var device = new FakeVivoEarbuds();
        var (controller, store) = Create(device, "vivo TWS 4", allowExperimental);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var profile = Assert.IsType<VivoDeviceProfile>(store.Current.Profile);
        Assert.Equal(CapabilitySupport.Experimental, profile.Capabilities.Get(DeviceCapability.TouchCustomization).Support);
        Assert.Equal(allowExperimental, profile.Toggles.Contains(DeviceToggle.InEarDetection));
        var result = await controller.SetKeyFunctionAsync(new KeyFunction(EarSide.Left, 0, GestureAction.DoubleSqueeze, 0x02), Ct);
        Assert.Equal(allowExperimental ? CommandStatus.Success : CommandStatus.Unsupported, result.Status);
        // The TWS 4 declares no dual connection list: it is never queried.
        Assert.DoesNotContain(device.Received, f => f.Command == VivoCommands.QuerySourceDevices);
        Assert.All(device.Received.Where(f => f.Command == VivoCommands.SetDoubleTap), f => Assert.Equal(4, device.VersionOf(f)));
    }

    [Theory]
    [InlineData("00 1B 85 09 00", 0x8509, "00")]
    [InlineData("00 1B 05 09 14 1A 08 06 0B 0C 1D", 0x0509, "14 1A 08 06 0B 0C 1D")]
    public void Gatt_DecodesTheHeaderlessTws5Captures(string wire, int command, string payload)
    {
        var frame = VivoFrame.DecodeGatt(Hex(wire));
        Assert.NotNull(frame);
        Assert.Equal((ushort)command, frame.Command);
        Assert.Equal(Hex(payload), frame.Payload);
        Assert.Equal(Hex(wire), frame.EncodeGatt(3));
        // Too short, or another vendor: not a vivo message.
        Assert.Null(VivoFrame.DecodeGatt(Hex("00 1B 85")));
        Assert.Null(VivoFrame.DecodeGatt(Hex("00 1C 85 09 00")));
    }

    [Fact]
    public async Task Tws5_OverGatt_SendsHeaderlessFramesAndReadsTheList()
    {
        await using var device = new FakeVivoEarbuds { Gatt = true, Sources = new VivoFrame(0x8249, Hex(Tws5DeviceList)[4..]).Payload };
        var (controller, store) = Create(device, "vivo TWS 5", deviceId: "Bluetooth#Bluetoothaa:bb:cc:44:55:66-12:34:56:78:9a:bc");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        Assert.All(device.Channel.Written, w => Assert.NotEqual(0xFF, w[0]));
        Assert.Contains(device.Channel.Written, w => w.SequenceEqual(Hex("00 1B 02 49")));
        Assert.Equal(3, store.Current.SourceDevices!.Value.Count);
        Assert.NotNull(store.Current.Battery.Left);
    }

    [Fact]
    public async Task Tws5_ManagesTheDualConnectionList()
    {
        await using var device = new FakeVivoEarbuds { Sources = new VivoFrame(0x8249, Hex(Tws5DeviceList)[4..]).Payload };
        var (controller, store) = Create(device, "vivo TWS 5", deviceId: "Bluetooth#Bluetoothaa:bb:cc:44:55:66-12:34:56:78:9a:bc");
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        var profile = Assert.IsType<VivoDeviceProfile>(store.Current.Profile);
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.DualDeviceConnection).Support);
        var listed = store.Current.SourceDevices!.Value;
        Assert.Equal(3, listed.Count);
        Assert.True(listed[0].IsThisDevice);
        Assert.True(listed[0].IsConnected);
        Assert.True(listed[0].IsAudioActive);
        Assert.Equal(1, listed[1].DeviceType);
        Assert.False(listed[1].IsConnected);

        byte[] phone = Hex("DD EE FF 77 88 99"), pad = Hex("11 22 33 AA BB CC"), pc = Hex("AA BB CC 44 55 66");
        Assert.True((await controller.MultiConnectAsync(MultiConnectOperation.Connect, phone, Ct)).IsSuccess);
        // Whole list; the playing PC keeps its reported 02, only the phone changes.
        Assert.Equal(Hex("AA BB CC 44 55 66 02 DD EE FF 77 88 99 02 11 22 33 AA BB CC 00"), device.Last(VivoCommands.SetSourceDevices).Payload);
        Assert.True(store.Current.SourceDevices!.Value.Single(d => d.Address.SequenceEqual(phone)).IsConnected);

        Assert.Equal(CommandStatus.Unsupported, (await controller.MultiConnectAsync(MultiConnectOperation.Disconnect, pc, Ct)).Status);
        Assert.Equal(CommandStatus.Rejected, (await controller.MultiConnectAsync(MultiConnectOperation.Remove, phone, Ct)).Status);

        Assert.True((await controller.MultiConnectAsync(MultiConnectOperation.Disconnect, phone, Ct)).IsSuccess);
        Assert.Equal(Hex("AA BB CC 44 55 66 02 DD EE FF 77 88 99 00 11 22 33 AA BB CC 00"), device.Last(VivoCommands.SetSourceDevices).Payload);

        Assert.True((await controller.MultiConnectAsync(MultiConnectOperation.Remove, pad, Ct)).IsSuccess);
        Assert.Equal(Hex("11 22 33 AA BB CC AA BB CC 44 55 66"), device.Last(VivoCommands.RemoveSourceDevice).Payload);
        Assert.Equal(2, store.Current.SourceDevices!.Value.Count);
        Assert.Equal(CommandStatus.Unsupported, (await controller.MultiConnectAsync(MultiConnectOperation.Connect, pad, Ct)).Status);
    }

    private static (IDeviceController Controller, DeviceStateStore Store) Create(FakeVivoEarbuds device, string name,
        bool allowExperimental = false, string deviceId = "dev-vivo")
    {
        var identity = new DeviceIdentity(deviceId, name, new BluetoothAddress(0x112233445566));
        var profile = (VivoDeviceProfile)new DeviceProfileRegistry(VivoProfiles.CreateBuiltIn()).Match(identity)!;
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile, Connection = DeviceConnectionState.ControlConnecting });
        var context = new ControllerContext(identity, store, () => allowExperimental, NullLoggerFactory.Instance, TimeProvider.System);
        return (profile.CreateController(device.Link, context), store);
    }

    /// <summary>
    /// vivo earbuds that answer the registration frames with gesture / wear-switch reports (as the
    /// TWS 3e does), apply writes and push the matching report, like the captured sessions.
    /// </summary>
    private sealed class FakeVivoEarbuds : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<(VivoFrame Frame, int Version)> _received = [];
        private byte _tapLeft = 0x01, _tapRight = 0x11, _hold = 0x02, _cycleLeft = 0x0B, _cycleRight = 0x0A, _wear = 1;

        public FakeVivoEarbuds() => _loop = Task.Run(LoopAsync);

        public FakeByteChannel Channel { get; } = new();

        /// <summary>Speak the GATT form (no <c>FF</c> header, one message per write / notification).</summary>
        public bool Gatt { get; init; }

        public IDuplexByteChannel Link => Gatt ? new GattLink(Channel) : Channel;

        public bool IgnoreWrites { get; init; }

        public byte[]? Sources { get; set; }

        public IReadOnlyList<VivoFrame> Received
        {
            get { lock (_received) return _received.Select(r => r.Frame).ToList(); }
        }

        public VivoFrame Last(ushort command) => Received.Last(f => f.Command == command);

        public int VersionOf(VivoFrame frame)
        {
            lock (_received) return _received.First(r => ReferenceEquals(r.Frame, frame)).Version;
        }

        private async Task LoopAsync()
        {
            int seen = 0;
            while (!_stop.IsCancellationRequested)
            {
                var written = Channel.Written;
                for (; seen < written.Count; seen++)
                {
                    IReadOnlyList<VivoFrame> frames = Gatt
                        ? [VivoFrame.DecodeGatt(written[seen]) ?? throw new InvalidOperationException("Non-GATT write")]
                        : new VivoFrameDecoder().Feed(written[seen]);
                    foreach (var frame in frames)
                    {
                        lock (_received) _received.Add((frame, Gatt ? 0 : written[seen][1]));
                        foreach (var reply in Answer(frame)) Channel.Receive(Gatt ? reply.EncodeGatt(3) : reply.Encode(3));
                    }
                }
                try { await Task.Delay(5, _stop.Token); } catch (OperationCanceledException) { }
            }
        }

        private IEnumerable<VivoFrame> Answer(VivoFrame f)
        {
            var p = f.Payload;
            switch (f.Command)
            {
                case VivoCommands.Handshake: yield return new(VivoCommands.HandshakeResponse, [0]); break;
                case VivoCommands.QueryBattery: yield return new(VivoCommands.ReportBattery, [0, 0x5C, 0x59, 0x48, 0x05]); break;
                case VivoCommands.QueryWearState: yield return new(VivoCommands.ReportWearState, [0, 0x0C]); break;
                case VivoCommands.QueryNoiseMode: yield return new(VivoCommands.ReportNoiseMode, [0, 1, 3]); break;
                case VivoCommands.QueryAudioEffect: yield return new(VivoCommands.ReportAudioEffect, [0, 0]); break;
                case VivoCommands.QueryLowLatency: yield return new(VivoCommands.ReportLowLatency, [0, 0]); break;
                case VivoCommands.QueryFirmware: yield return new(VivoCommands.ReportFirmware, [0, 1, 2, 3, 4, 5, 9, 0x0C]); break;
                case VivoCommands.QueryDoubleTap: yield return DoubleTap(); break;
                case VivoCommands.QueryWearDetection: yield return new(VivoCommands.ReportWearDetection, [0, _wear]); break;
                case VivoCommands.QueryLongPress: yield return LongPress(); break;
                case VivoCommands.QueryNoiseCycle when p is [VivoConstants.NoiseCycleSubCommand]: yield return Cycle(); break;
                case VivoCommands.QuerySourceDevices when Sources is not null: yield return new(VivoCommands.ReportSourceDevices, Sources); break;
                case VivoCommands.SetDoubleTap when !IgnoreWrites && p is [var code]:
                    if (code >= 0x10) _tapRight = code; else _tapLeft = code;
                    yield return DoubleTap();
                    break;
                case VivoCommands.SetLongPress when !IgnoreWrites && p is [VivoConstants.LongPressSubCommand, var hold]:
                    _hold = hold;
                    if (hold != VivoGestures.LongPressNoiseControl) (_cycleLeft, _cycleRight) = (0xFF, 0xFF);
                    yield return new(VivoCommands.AckLongPress, [0, 3, hold]);
                    yield return LongPress();
                    break;
                case VivoCommands.SetNoiseCycle when !IgnoreWrites && p is [VivoConstants.NoiseCycleSubCommand, var l, var r]:
                    (_cycleLeft, _cycleRight) = (l, r);
                    yield return Cycle();
                    break;
                case VivoCommands.SetWearDetection when !IgnoreWrites && p is [var on]:
                    _wear = on;
                    yield return new(VivoCommands.ReportWearDetection, [0, _wear]);
                    break;
                case VivoCommands.SetSourceDevices when Sources is not null:
                    Sources = ApplyTable(Sources, p);
                    yield return new(VivoCommands.ReportSourceDevices, Sources);
                    break;
                case VivoCommands.RemoveSourceDevice when Sources is not null && p.Length == 12:
                    Sources = Remove(Sources, p[..6]);
                    yield return new(VivoCommands.ReportSourceDevices, Sources);
                    break;
            }
        }

        private VivoFrame DoubleTap() => new(VivoCommands.ReportDoubleTap, [0, _tapLeft, _tapRight]);

        private VivoFrame LongPress() => new(VivoCommands.ReportLongPress, [0, 3, _hold]);

        private VivoFrame Cycle() => new(VivoCommands.ReportNoiseCycle, [0, 5, _cycleLeft, _cycleRight]);

        private static byte[] ApplyTable(byte[] list, byte[] table)
        {
            Assert.True(VivoParsers.TryParseSourceDevices(list, out var devices));
            var copy = list.ToArray();
            int offset = 2;
            foreach (var device in devices)
            {
                for (int i = 0; i < table.Length; i += 7)
                {
                    if (table.AsSpan(i, 6).SequenceEqual(device.Address)) copy[offset + 9] = table[i + 6];
                }
                offset += 11 + System.Text.Encoding.UTF8.GetByteCount(device.Name);
            }
            return copy;
        }

        private static byte[] Remove(byte[] list, byte[] address)
        {
            Assert.True(VivoParsers.TryParseSourceDevices(list, out var devices));
            var result = new List<byte> { 0, 0 };
            int offset = 2;
            byte count = 0;
            foreach (var device in devices)
            {
                int length = 11 + System.Text.Encoding.UTF8.GetByteCount(device.Name);
                if (!device.Address.AsSpan().SequenceEqual(address))
                {
                    result.AddRange(list.AsSpan(offset, length).ToArray());
                    count++;
                }
                offset += length;
            }
            result[1] = count;
            return [.. result];
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
        }
    }

    /// <summary>The fake device behind a channel that reports itself as GATT.</summary>
    private sealed class GattLink(FakeByteChannel inner) : IDuplexByteChannel
    {
        public string Description => "vivo GAIA GATT (test)";

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => inner.WriteAsync(data, cancellationToken);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => inner.ReadAsync(buffer, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
