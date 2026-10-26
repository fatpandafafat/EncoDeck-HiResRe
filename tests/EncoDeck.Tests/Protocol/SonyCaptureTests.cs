// SPDX-License-Identifier: GPL-3.0-only
// The MDR frames below are real-device captures from mos9527/SonyHeadphonesClient
// (https://github.com/mos9527/SonyHeadphonesClient, revision 4061cf353fd0044e559e9a1f1b7f2faa334d09c6,
// tests/WH-1000XM3-4.5.2, tests/WF-1000XM5-6.1.0, tests/WF-LC900-2.0.3-equalizer).
// MIT License, Copyright (c) 2026 mos9527, Amr Satrio and other contributors; the licence text is
// reproduced in THIRD_PARTY_NOTICES.md. Frames carrying paired-device names, addresses or track
// metadata were not taken.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed class SonyCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Xm3Frames =
    [
        "3E0C000000000401004010613C", // CONNECT_RET_PROTOCOL_INFO (MDR v1)
        "3E0C0100000008050205342E352E32183C", // CONNECT_RET_DEVICE_INFO firmware "4.5.2"
        "3E0C010000001607001311C112131430716281424151A1E1E2D1D2F439D43C", // CONNECT_RET_SUPPORT_FUNCTION
        "3E0C000000001D510106150C000010001100120013001400150016001700A000A100A200213C", // EQEBB_RET_CAPABILITY (empty names)
        "3E0C0000000008F104050001020311253C", // SYSTEM_RET_CAPABILITY auto power off 5/30/60/180/off
        "3E0C01000000D7D1D1021641535349474E41424C455F4B45595F53455454494E47000203021941535349474E41424C455F4B45595F454C454D5F4E4341534D00022441535349474E41424C455F4B45595F454C454D5F474F4F474C455F415353495354414E542C41535349474E41424C455F4B45595F454C454D5F474F4F474C455F415353495354414E545F53554D4D415259022041535349474E41424C455F4B45595F454C454D5F414D415A4F4E5F414C4558412841535349474E41424C455F4B45595F454C454D5F414D415A4F4E5F414C4558415F53554D4D4152595A3C", // GS1 list
        "3E0C0000000019D1D20213544F5543485F50414E454C5F53455454494E470001AD3C", // GS2 TOUCH_PANEL_SETTING (boolean)
        "3E0C010000000A570100060A0A0A0A0A0AB13C", // EQEBB_RET_PARAM preset 0
        "3E0C00000000086702010202010000833C", // NCASM_RET_PARAM noise cancelling
        "3E0C0000000004E7020001FA3C", // AUDIO_RET_PARAM upscaling auto
        "3E0C0000000005F7040100000D3C", // SYSTEM_RET_PARAM auto power off 5 min
        "3E0C010000000411006400863C", // battery 100 %, not charging
        "3E0C0100000004D7D10200BB3C", // GS1 value
        "3E0C0100000004D7D20101BC3C", // GS2 touch panel on
    ];

    private static byte[] Payload(string frame) => Assert.Single(new SonyFrameCodec().Feed(Convert.FromHexString(frame))).Payload;

    /// <summary>
    /// Replays captured replies. Like the WH-1000XM3 it answers each request before acknowledging it
    /// (mos9527/SonyHeadphonesClient PR #66); writes update the reply the next read returns.
    /// </summary>
    private sealed class ReplayPeer : IDuplexByteChannel
    {
        private readonly FakeByteChannel _channel = new();
        private readonly Dictionary<(byte, byte), byte[]> _replies = [];
        private byte _sequence;
        public bool AckBeforeReply { get; init; }
        public List<SonyFrame> Requests { get; } = [];
        public string Description => "Sony capture replay";
        public ReplayPeer(IEnumerable<byte[]> replies)
        {
            foreach (var p in replies)
            {
                // Map a reply back to its request: command - 1 and the inquired type.
                byte request = p[0] switch { 0x11 => 0x10, _ => (byte)(p[0] - 1) };
                _replies[(request, p[0] == 1 || p[0] == 7 ? (byte)0 : p[1])] = p;
            }
        }

        public void Set(byte request, byte type, byte[] reply) => _replies[(request, type)] = reply;
        public byte[]? Reply(byte request, byte type) => _replies.GetValueOrDefault((request, type));

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            var frame = Assert.Single(new SonyFrameCodec().Feed(data.Span));
            if (frame.Type == 1) return ValueTask.CompletedTask;
            Requests.Add(frame);
            byte[] p = frame.Payload;
            byte[]? reply = p[0] switch
            {
                0xFC => Update(0xF6, p[1], [0xF7, .. p[1..]]),
                0xE8 => Update(0xE6, p[1], [0xE7, .. p[1..]]),
                0xD8 => Update(0xD6, p[1], [0xD7, .. p[1..]]),
                0x68 => Update(0x66, p[1], [0x67, .. p[1..]]),
                0x58 => Update(0x56, p[1], [0x57, p[1], p[2], 0]),
                _ => p.Length >= 2 ? Reply(p[0], p[0] is 0 or 6 ? (byte)0 : p[1]) : null,
            };
            byte[] ack = SonyFrameCodec.Encode(1, (byte)(frame.Sequence ^ 1), []);
            if (AckBeforeReply) _channel.Receive(ack);
            if (reply is not null) { _channel.Receive(SonyFrameCodec.Encode(12, _sequence, reply)); _sequence ^= 1; }
            if (!AckBeforeReply) _channel.Receive(ack);
            return ValueTask.CompletedTask;
        }

        private byte[]? Update(byte request, byte type, byte[] reply) { _replies[(request, type)] = reply; return null; }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _channel.ReadAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync() => _channel.DisposeAsync();
    }

    private static (DeviceStateStore Store, IDeviceController Controller) Start(string model, IDuplexByteChannel peer)
    {
        var identity = new DeviceIdentity("sony-capture", model, new BluetoothAddress(1));
        var profile = (IControllableProfile)SonyAkgProfiles.CreateBuiltIn().Single(p => p.DisplayName == model);
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        return (store, profile.CreateController(peer, new(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System)));
    }

    [Theory]
    [InlineData(false)] // reply before ACK, as captured from the WH-1000XM3 4.5.2
    [InlineData(true)]
    public async Task Xm3Capture_InitializesAndConfirmsSettingsByReadback(bool ackFirst)
    {
        var peer = new ReplayPeer(Xm3Frames.Select(Payload)) { AckBeforeReply = ackFirst };
        var (store, controller) = Start("WH-1000XM3", peer);
        await using var _ = controller;
        Assert.True(await controller.InitializeAsync(Ct));

        // Nothing was retransmitted: every reply and ACK was matched on the first attempt.
        Assert.Equal(peer.Requests.Count, peer.Requests.Select(r => Convert.ToHexString(r.Payload)).Distinct().Count());
        Assert.Equal("4.5.2", store.Current.Firmware?.Value.Version);
        Assert.Equal(new BatteryReading(100, false), store.Current.Battery.Left?.Value);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
        Assert.False(store.Current.Options.ContainsKey("sony-ambient-level"));
        var profile = store.Current.Profile!;
        Assert.Equal(CapabilitySupport.Supported, profile.Capabilities.Get(DeviceCapability.Equalizer).Support);
        // The XM3 sends every preset name empty; EncoDeck labels the known ids.
        Assert.Equal(["Off", "Bright", "Excited", "Mellow", "Relaxed", "Vocal", "Treble Boost", "Bass Boost", "Speech", "Custom", "User Setting 1", "User Setting 2"],
            profile.EqualizerPresets.Select(p => p.DisplayName));
        Assert.Equal("SonyEq_Bright", profile.EqualizerPresets.Single(p => p.Id == "sony-16").ResourceKey);
        Assert.Equal("sony-0", store.Current.Equalizer?.Value.ActivePresetId);
        Assert.True(store.Current.Toggles[DeviceToggle.Upscaling].Value);
        Assert.True(store.Current.Toggles[DeviceToggle.TouchControls].Value);
        var controllable = Assert.IsAssignableFrom<IControllableProfile>(profile);
        Assert.Contains(DeviceToggle.TouchControls, controllable.Toggles);
        Assert.DoesNotContain(DeviceToggle.SpeakToChat, controllable.Toggles); // the XM3 advertises no Speak-to-Chat
        Assert.DoesNotContain(DeviceToggle.InEarDetection, controllable.Toggles);
        // GS1 is a list (NC/AMB button assignment), not offered as a switch.
        Assert.Single(controllable.Toggles, t => t is DeviceToggle.TouchControls or DeviceToggle.DualDeviceConnection);

        var autoOff = store.Current.Options["sony-auto-power-off"].Value;
        Assert.Equal(0, autoOff.Value);
        // v1 has no 15-minute element.
        Assert.Equal([0, 1, 2, 3, 0x11], autoOff.Choices.Select(c => c.Value));
        Assert.Equal("30", autoOff.Choices[1].Argument);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetOptionAsync("sony-auto-power-off", 4, Ct)).Status);
        Assert.True((await controller.SetOptionAsync("sony-auto-power-off", 0x11, Ct)).IsSuccess);
        // Written with SYSTEM_SET_EXTENDED_PARAM, as confirmed on the XM3.
        Assert.Equal(new byte[] { 0xFC, 4, 1, 0x11, 0x11 }, peer.Requests.Last(r => r.Payload[0] == 0xFC).Payload);
        Assert.Equal(0x11, store.Current.Options["sony-auto-power-off"].Value.Value);

        Assert.True((await controller.SetToggleAsync(DeviceToggle.Upscaling, false, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0xE8, 2, 0, 0 }, peer.Requests.Last(r => r.Payload[0] == 0xE8).Payload);
        Assert.False(store.Current.Toggles[DeviceToggle.Upscaling].Value);
        Assert.True((await controller.SetToggleAsync(DeviceToggle.TouchControls, false, Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0xD8, 0xD2, 1, 0 }, peer.Requests.Last(r => r.Payload[0] == 0xD8).Payload);
        Assert.Equal(CommandStatus.Unsupported, (await controller.SetToggleAsync(DeviceToggle.SpeakToChat, true, Ct)).Status);

        // Ambient sound uses the level-adjustment frame confirmed on the XM3.
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.Transparency), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x68, 2, 0x11, 1, 0, 1, 0, 20 }, peer.Requests.Last(r => r.Payload[0] == 0x68).Payload);
        Assert.Equal(SonyAkgProfile.SonyAmbient, store.Current.NoiseControl?.Value.LevelId);
        Assert.True((await controller.SetNoiseControlAsync(new(NoiseControlMode.NoiseCancellation), Ct)).IsSuccess);
        Assert.Equal(new byte[] { 0x68, 2, 0x11, 1, 2, 1, 0, 0 }, peer.Requests.Last(r => r.Payload[0] == 0x68).Payload);
        Assert.False(store.Current.Options.ContainsKey("sony-ambient-level"));
    }

    [Fact]
    public void Xm5Capture_V2LayoutsParse()
    {
        Assert.Equal(8, Payload("3E0C01000000080100030030180000613C").Length); // v2 protocol info
        Assert.Equal("6.1.0", SonySettings.TryDeviceInfo(Payload("3E0C0100000008050205362E312E30143C"), 2));
        Assert.Equal("WF-1000XM5", SonySettings.TryDeviceInfo(Payload("3E0C000000000D05010A57462D31303030584D358E3C"), 1));
        Assert.True(SonySettings.TryFunctions(Payload("3E0C000000005F07002E10FF12FF13FF14FF15FF16FF17FF23FF253611FF27FF29FF2AFF92FF50116B08700190FFA112C1FFC2FFE21BF138FC0D93FFF633F941D13B94FF402E423A44FFFF2D19FFF326E71AD220FD2BB61DB042B243B81FB715343FF423F524293C"), true, out var functions));
        Assert.Equal(46, functions.Count);
        Assert.Superset(new HashSet<byte> { 0x6B, 0x50, 0xE2, 0xF1, 0xFC, 0xD1, 0xD2, 0xF3, 0x25 }, functions);
        Assert.Equal((byte?)23, SonyV2Noise.InquiredType(0x6B));
        Assert.Equal(3, SonySettings.BooleanSettings(true, functions).Count); // DSEE, pause when removed, Speak-to-Chat

        // NC/ASM 0x17: total effect off, ambient mode normal, stored level 20.
        Assert.True(SonyV2Noise.TryRead(Payload("3E0C010000000767170100010014A83C"), out var mode, out bool voice, out int level));
        Assert.Equal((NoiseControlMode.Off, false, 20), (mode, voice, level));

        var settings = SonySettings.BooleanSettings(true, functions).ToDictionary(s => s.Toggle);
        Assert.True(settings[DeviceToggle.Upscaling].Parse(Payload("3E0C0100000003E70101F93C")));
        Assert.True(settings[DeviceToggle.InEarDetection].Parse(Payload("3E0C0100000003F70100083C"))); // 0 = ON in v2
        Assert.False(settings[DeviceToggle.SpeakToChat].Parse(Payload("3E0C0000000004F70C0101153C")));

        Assert.Equal("SIDETONE_SETTING", SonySettings.TryGeneralSettingName(Payload("3E0C000000002ED1D100011053494445544F4E455F53455454494E471853494445544F4E455F53455454494E475F53554D4D415259423C"), true, 0xD1));
        Assert.Null(SonySettings.GeneralSettingToggle("SIDETONE_SETTING"));
        string? multipoint = SonySettings.TryGeneralSettingName(Payload("3E0C0000000041D1D20001124D554C5449504F494E545F53455454494E47294D554C5449504F494E545F53455454494E475F53554D4D4152595F4C4441435F415641494C41424C45303C"), true, 0xD2);
        Assert.Equal(DeviceToggle.DualDeviceConnection, SonySettings.GeneralSettingToggle(multipoint!));
        Assert.True(SonySettings.TryGeneralSettingValue(Payload("3E0C0100000004D7D20000BA3C"), true, 0xD2));
        Assert.False(SonySettings.TryGeneralSettingValue(Payload("3E0C0100000004D7D10001BA3C"), true, 0xD1));
        Assert.Equal(new byte[] { 0xD2, 0, 1 }, SonySettings.GeneralSettingPayload(true, 0xD2, false));

        // Auto power off with wearing detection currently "when taken off".
        var autoOff = Payload("3E0C0000000004270510004C3C");
        Assert.Equal(SonySettings.AutoOffWhenRemoved, autoOff[2]);
        Assert.True(SonySettings.IsAutoOffElement(autoOff[2], true, wearing: true));
        Assert.False(SonySettings.IsAutoOffElement(autoOff[2], true, wearing: false));
        Assert.True(SonyEqualizer.TryParameter(Payload("3E0C010000000A5700A006140B0A0D090C5F3C"), out byte preset));
        Assert.Equal((byte)0xA0, preset);
    }

    [Fact]
    public void Lc900Capture_EmptyPresetNamesGetLabelsAndXm6KeepsOnlyAcceptedPresets()
    {
        Assert.True(SonyEqualizer.TryPresets(Payload("3E0C000000001551000A0D0800003000310032003300A000A100A2003A3C"), out var presets));
        // MDR v2 names the user slots like the current Sony app (WH-1000XM6): Manual, Custom 1, Custom 2.
        Assert.Equal(["Off", "Heavy", "Clear", "Hard", "Soft", "Manual", "Custom 1", "Custom 2"], presets.Select(p => p.DisplayName));
        Assert.Equal("SonyEq_Heavy", presets[1].ResourceKey);
        Assert.True(SonyEqualizer.TryParameter(Payload("3E0C010000000E5700300A0A0A0505060606060606EE3C"), out byte heavy));
        Assert.Equal((byte)0x30, heavy);
        Assert.True(SonyEqualizer.TryParameter(Payload("3E0C000000000E5900A00A06060606060606060606593C"), out byte custom));
        Assert.Equal((byte)0xA0, custom);
        // WH-1000XM6 (PR #48): only Heavy/Clear/Hard/Soft, Custom and the user settings.
        Assert.Equal([0x30, 0x31, 0x32, 0x33, 0xA0, 0xA1, 0xA2],
            presets.Select(p => byte.Parse(p.Id.AsSpan(5))).Where(id => SonySettings.IsPresetAccepted("WH-1000XM6", id)).Select(id => (int)id));
        Assert.True(SonySettings.IsPresetAccepted("WF-LC900", 0x00));
    }

    [Fact]
    public void AssignableCapabilityParsesPerKeyPresetsAndRejectsTruncation()
    {
        // Two touch sensors: left = ambient control / quick access, right = playback / volume.
        // Each preset: [preset, nActions, nCustomizable, (action, function)…, (action, default, n, functions…)…].
        byte[] capability =
        [
            0xF1, 3, 2,
            0, 0, 0x35, 2,
                0x35, 1, 0, 0x00, 0x30,
                0x00, 1, 1, 0x00, 0x30, 0x01, 0x10, 1, 0x40,
            1, 0, 0x20, 2,
                0x20, 2, 0, 0x00, 0x20, 0x01, 0x21,
                0x10, 1, 0, 0x00, 0x10,
        ];
        var keys = SonySettings.TryAssignableCapability(capability);
        Assert.NotNull(keys);
        Assert.Equal([0x35, 0x00], keys![0].Presets.Select(p => (int)p));
        Assert.Equal([0x20, 0x10], keys[1].Presets.Select(p => (int)p));
        Assert.Null(SonySettings.TryAssignableCapability(capability[..^1]));
        // Real WF-1000XM5 6.1.0 assignment reply: left 0x35, right 0x20.
        Assert.Equal(new byte[] { 0x35, 0x20 }, SonySettings.TryAssignablePresets(Payload("3E0C0000000005F703023520623C"), 2));
    }

    [Theory]
    [InlineData("WF-C700N", "2.0.5", true)]
    [InlineData("WF-C700N", "2.0.7", false)]
    [InlineData("WF-1000XM5", "2.0.5", false)]
    public void C700nFirmware205IgnoresNoiseWrites(string model, string firmware, bool ignored) =>
        Assert.Equal(ignored, SonySettings.IgnoresNoiseWrites(model, firmware));

    [Fact]
    public void GattStreamFallbackIsLastForSonyAndFirstForAkg()
    {
        var sony = (IControllableProfile)SonyAkgProfiles.CreateBuiltIn().Single(p => p.DisplayName == "WH-1000XM6");
        Assert.Equal([SonyAkgProfiles.PrimaryServiceUuid, SonyAkgProfiles.LegacyServiceUuid, SonyAkgProfiles.BleServiceUuid], sony.ControlServiceUuids);
        // Sony's GATT service is opened by the dedicated LE Audio MDR channel, not the shared stream channel.
        Assert.Null(GattStreamEndpoints.Find(SonyAkgProfiles.BleServiceUuid));
        Assert.NotNull(GattStreamEndpoints.Find(EncoDeck.Protocol.Brands.Akg.AkgBleUuids.BesService));
        Assert.False(SonyAkgProfiles.IsControlService(SonyAkgProfiles.BleServiceUuid));
    }
}
