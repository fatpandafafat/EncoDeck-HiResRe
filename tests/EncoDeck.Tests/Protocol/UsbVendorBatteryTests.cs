// SPDX-License-Identifier: GPL-3.0-only
// Fixtures: replies marked "real" were captured on hardware and published by HeyOkay/HaloBattery
// (MIT, Copyright (c) 2026 Halo Battery contributors: HyperX Cloud Alpha 2 issue #26, SteelSeries Arctis
// Nova 7 1038:22A1), SnowDreamXUE/NPS.HecateBattery (MIT, Copyright (c) 2026 SnowDream: HECATE G3 MAX
// docs/G3MAX_BATTERY_PROTOCOL.md) and Tensyd/mchose-battery (MCHOSE V9 PRO, facts only). Request bytes
// follow the same sources plus Sapd/HeadsetControl (GPL-3.0) and TarikTopalovic/razer-barracuda-2.4-linux
// (facts only).
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.Resources;
using EncoDeck.Protocol.Brands.Usb;

namespace EncoDeck.Tests.Protocol;

public sealed class UsbVendorBatteryTests
{
    [Theory]
    [InlineData(0x3837, 0x6045, 0xFF90, true)]
    [InlineData(0x3837, 0x6045, 0xFF00, false)]
    [InlineData(0x3837, 0x6046, 0xFF90, false)]
    [InlineData(0x291D, 0x385D, 0xFF90, true)]
    [InlineData(0x291D, 0x385E, 0xFF90, false)]
    [InlineData(0x1532, 0x0528, 0xFF00, true)]
    [InlineData(0x1532, 0x0555, 0xFF00, true)]
    [InlineData(0x1532, 0x0556, 0xFF00, false)]
    [InlineData(0x1532, 0x052E, 0xFF00, false)]
    [InlineData(0x1532, 0x053A, 0xFF00, true)]
    [InlineData(0x1532, 0x053A, 0x000C, false)]
    [InlineData(0x1532, 0x053C, 0xFF00, true)]
    [InlineData(0x03F0, 0x0ABE, 0xFF13, false)]
    [InlineData(0x1038, 0x22A1, 0xFFC0, true)]
    [InlineData(0x1038, 0x2202, 0xFFC0, false)]
    [InlineData(0x0ECB, 0x2088, 0xFF13, true)]
    [InlineData(0x046D, 0x0AF7, 0xFFA0, true)]
    [InlineData(0x046D, 0x0AF7, 0xFF43, false)]
    [InlineData(0x1532, 0x0577, 0xFF00, false)]
    public void VendorQueriesAreGatedByExactReceiverAndCollection(int vendor, int product, int page, bool expected)
    {
        Assert.Equal(expected, UsbVendorBatteryProtocol.IsCollection((ushort)vendor, (ushort)product, (ushort)page, 1));
        Assert.False(UsbVendorBatteryProtocol.IsCollection((ushort)vendor, (ushort)product, (ushort)page, 2));
    }

    [Fact]
    public void CollectionsWithoutDocumentedUsageRequireVendorPageAndExactInterface()
    {
        Assert.True(UsbVendorBatteryProtocol.IsCollection(0x03F0, 0x08BE, 0xFF13, 0xFF00));
        Assert.False(UsbVendorBatteryProtocol.IsCollection(0x03F0, 0x08BE, 0xFF13, 1));
        Assert.True(UsbVendorBatteryProtocol.IsCollection(0x03F0, 0x02CC, 0xFF13, 1));
        Assert.True(UsbVendorBatteryProtocol.IsCollection(0x03F0, 0x06BE, 0xFFC0, 0x0202));
        Assert.False(UsbVendorBatteryProtocol.IsCollection(0x03F0, 0x02CC, 0x000C, 1));
        Assert.False(UsbVendorBatteryProtocol.IsCollection(0x35BB, 0xA226, 0x0001, 6));
        Assert.True(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_35BB&PID_A226&MI_03&COL03#8&1&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.False(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_35BB&PID_A226&MI_03&COL02#8&1&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.False(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_35BB&PID_A226&MI_03&COL04#8&1&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.True(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_1532&PID_0550&MI_03&COL01#8&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.True(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_046D&PID_0AF7&MI_03&COL02#8&1&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
        Assert.False(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_046D&PID_0AF70&MI_03#8&1&0001#{x}"));
        Assert.False(UsbVendorBatteryProtocol.IsInterface(@"\\?\HID#VID_1532&PID_0577&MI_05#8&1&0001#{x}"));
        Assert.Equal(64, UsbVendorBatteryProtocol.ReportLength(0x35BB, 0xA226));
        Assert.Null(UsbVendorBatteryProtocol.ReportLength(0x1038, 0x22A1));
    }

    [Theory]
    [InlineData(0x3837, 0x6045)]
    [InlineData(0x291D, 0x385D)]
    public void MchoseIgnoresHeartbeatAndOtherFieldsAndInvalidPercentages(int vendor, int product)
    {
        ushort v = (ushort)vendor, p = (ushort)product;
        Assert.Equal(new byte[] { 0x55, 0x65, 1, 0 }, UsbVendorBatteryProtocol.Query(v, p)[..4]);
        // Real V9 PRO reply (80 %); the X9 answers in the same layout.
        var frame = new byte[64]; frame[0] = 0x55; frame[1] = 0x65; frame[2] = 80; frame[3] = 2;
        Assert.Equal(80, UsbVendorBatteryProtocol.Parse(v, p, frame));
        Assert.Null(UsbVendorBatteryProtocol.Parse(0x3837, 0x6046, frame));
        Assert.Null(UsbVendorBatteryProtocol.Parse(v, p, frame[..4]));
        // The request itself, heartbeats and other telemetry fields are not the battery.
        Assert.Null(UsbVendorBatteryProtocol.Parse(v, p, UsbVendorBatteryProtocol.Query(v, p)));
        frame[3] = 1; Assert.Null(UsbVendorBatteryProtocol.Parse(v, p, frame));
        frame[3] = 3; Assert.Null(UsbVendorBatteryProtocol.Parse(v, p, frame));
        frame[3] = 2; frame[2] = 255; Assert.Null(UsbVendorBatteryProtocol.Parse(v, p, frame));
    }

    [Fact]
    public void Blackshark2020RejectsCachedOfflineAndUnreliableChargingLevels()
    {
        var frame = Convert.FromHexString("ff0f05fe12041f08050305010ec050").Concat(new byte[49]).ToArray();
        Assert.Equal(80, UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        frame[11] = 9; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        frame[11] = 6; frame[14] = 100; Assert.Equal(100, UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        frame[1] = 1; frame[2] = 0; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0528, frame));
        Assert.Equal(Convert.FromHexString("ff0a00fd0412f10205"), UsbVendorBatteryProtocol.Query(0x1532, 0x0528)[..9]);
    }

    [Fact]
    public void Blackshark2023MatchesBatteryAckRatherThanRemoteOrChargingAck()
    {
        Assert.Equal(Convert.FromHexString("028008000050410800032100"), UsbVendorBatteryProtocol.Query(0x1532, 0x0555)[..12]);
        Assert.Equal(Convert.FromHexString("028007000050410E0002E101"), UsbVendorBatteryProtocol.Remote(true)[..12]);
        // Replies carry 'P','I' at [2]..[3], the echoed command at [12], ACK 01, length and the value.
        var frame = new byte[64]; frame[0] = 2; frame[1] = 14; frame[2] = 0x50; frame[3] = 0x49; frame[4] = 8;
        frame[12] = 0x21; frame[13] = 1; frame[14] = 1; frame[15] = 65;
        Assert.Equal(65, UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        Assert.Null(UsbVendorBatteryProtocol.ReadPa(0x0555, frame, 0x2A));
        frame[12] = 0x2A; frame[15] = 2;
        Assert.Equal(new UsbHeadsetReading(IsCharging: true), UsbVendorBatteryProtocol.ReadPa(0x0555, frame, 0x2A));
        frame[15] = 0; Assert.Equal(new UsbHeadsetReading(IsCharging: false), UsbVendorBatteryProtocol.ReadPa(0x0555, frame, 0x2A));
        Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[12] = 0x21; frame[13] = 2; frame[15] = 65; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[13] = 1; frame[14] = 49; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[14] = 1; frame[15] = 101; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        frame[15] = 65; frame[2] = 0; Assert.Null(UsbVendorBatteryProtocol.Parse(0x1532, 0x0555, frame));
        Assert.Equal(1, UsbVendorBatteryProtocol.Remote(true)[11]);
        Assert.Equal(0, UsbVendorBatteryProtocol.Remote(false)[11]);
        Assert.Throws<ArgumentException>(() => UsbVendorBatteryProtocol.Query(0x1532, 0x0556));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsbVendorBatteryProtocol.Query(0x1532, 0x0555, 0x93));
    }

    [Theory]
    [InlineData(0x053A)]
    [InlineData(0x053C)]
    public void BarracudaReceiversUseTheCapturedPaFrames(int product)
    {
        ushort p = (ushort)product;
        // Byte for byte the frames HaloBattery (053A) and razer-barracuda-2.4-linux (053C) send.
        Assert.Equal(Convert.FromHexString("01800750410E0802E101"), UsbVendorBatteryProtocol.Remote(p, true)[..10]);
        Assert.Equal(Convert.FromHexString("01800850410808032100"), UsbVendorBatteryProtocol.Query(0x1532, p, 0x21)[..10]);
        Assert.Equal(Convert.FromHexString("01800850410808032A00"), UsbVendorBatteryProtocol.Query(0x1532, p, 0x2A)[..10]);
        Assert.All(UsbVendorBatteryProtocol.Query(0x1532, p, 0x21)[11..], b => Assert.Equal(0, b));
        // 'P','I' at [3], command echo at [13], reply 01, length 01, value.
        var frame = new byte[64];
        Convert.FromHexString("01800E50490800000000000400210101").CopyTo(frame, 0);
        frame[16] = 34;
        Assert.Equal(new UsbHeadsetReading(Level: 34), UsbVendorBatteryProtocol.ReadPa(p, frame, 0x21));
        frame[14] = 2; Assert.Null(UsbVendorBatteryProtocol.ReadPa(p, frame, 0x21));
        frame[14] = 1; frame[1] = 0; Assert.Null(UsbVendorBatteryProtocol.ReadPa(p, frame, 0x21));
    }

    [Fact]
    public void HyperXCloudAlpha2ReadsTheCapturedReplies()
    {
        var request = UsbVendorBatteryProtocol.Requests(0x03F0, 0x08BE).Single();
        Assert.Equal(new byte[] { 0x50, 0x02 }, request.Report[..2]);
        Assert.All(request.Report[2..], b => Assert.Equal(0, b));
        // Real replies at 51 % and 67 %.
        byte[] at51 = [.. Convert.FromHexString("510233001a00000f01010a"), .. new byte[53]];
        byte[] at67 = [.. Convert.FromHexString("510243001a00860f01010a"), .. new byte[53]];
        Assert.Equal(new UsbHeadsetReading(Level: 51), request.Parse(at51));
        // Bit 7 of byte 6 followed a recharge but is not a confirmed charging flag.
        Assert.Equal(new UsbHeadsetReading(Level: 67), request.Parse(at67));
        // Byte-identical at both levels, so not the battery.
        byte[] constant = [.. Convert.FromHexString("61025a0001140000011000008001090100000030"), .. new byte[44]];
        Assert.Null(request.Parse(constant));
        Assert.Null(request.Parse([0xFF, 0x01, .. new byte[62]]));
        at51[2] = 0xFF; Assert.Null(request.Parse(at51));
        Assert.Null(request.Parse([0x51, 0x02, 0x33]));
    }

    [Fact]
    public void HyperXCloud3SAsksForValuesOnlyAndMatchesEachAnswer()
    {
        var requests = UsbVendorBatteryProtocol.Requests(0x03F0, 0x02CC);
        Assert.Equal(new byte[] { 0x0C, 2, 3, 1, 0, 0x06 }, requests[0].Report[..6]);
        Assert.Equal(new byte[] { 0x0C, 2, 3, 1, 0, 0x48 }, requests[1].Report[..6]);
        Assert.All(requests, r => Assert.Equal(1, r.Report[3]));
        byte[] Reply(byte command, byte value) => [0x0C, 2, 3, 1, 0, command, value, .. new byte[57]];
        Assert.Equal(new UsbHeadsetReading(Level: 89), requests[0].Parse(Reply(0x06, 89)));
        Assert.Null(requests[0].Parse(Reply(0x06, 0xFF)));
        Assert.Null(requests[0].Parse(Reply(0x48, 1)));
        Assert.Null(requests[0].Parse(Reply(0x04, 1)));
        Assert.Equal(new UsbHeadsetReading(IsCharging: true), requests[1].Parse(Reply(0x48, 2)));
        Assert.Equal(new UsbHeadsetReading(IsCharging: false), requests[1].Parse(Reply(0x48, 0)));
        Assert.Null(requests[1].Parse(Reply(0x48, 7)));
        Assert.Equal(new UsbHeadsetReading(Level: 55), requests[0].Parse([0x0D, 0, 0, 0, 1, 55, 0]));
        Assert.Equal(new UsbHeadsetReading(IsCharging: true), requests[1].Parse([0x0D, 0, 0, 0, 10, 1, 0]));
        Assert.Null(requests[0].Parse([0x0D, 0, 0, 0, 3, 1, 0]));
    }

    [Fact]
    public void ArctisNova7ReadsTheRealStatusFramesAndTrustsOnlyTheLinkByte()
    {
        var request = UsbVendorBatteryProtocol.Requests(0x1038, 0x22A1).Single();
        Assert.Equal(new byte[] { 0, 0xB0 }, request.Report);
        // Real 1038:22A1 replies; WinRT adds the zero report ID in front.
        byte[] Frame(params byte[] reply) => [0, .. reply];
        Assert.Equal(new UsbHeadsetReading(73, false, true), request.Parse(Frame(0xB0, 0x03, 0x49, 0x03, 0x1E, 0x64, 0, 0)));
        Assert.Equal(new UsbHeadsetReading(69, true, true), request.Parse(Frame(0xB0, 0x03, 0x45, 0x01, 0x22, 0x64, 0, 0)));
        // Headset off, and still switching on: the old level 0x49 is repeated and must not be shown.
        Assert.Equal(new UsbHeadsetReading(IsConnected: false), request.Parse(Frame(0xB0, 0x02, 0x49, 0x00, 0x64, 0x64, 0, 0)));
        Assert.Equal(new UsbHeadsetReading(IsConnected: false), request.Parse(Frame(0xB0, 0x02, 0x49, 0x03, 0x1E, 0x64, 0, 0)));
        // A stray report on the same collection would otherwise read as 99 %.
        Assert.Null(request.Parse(Frame(0x01, 0x00, 0x63, 0x02)));
        Assert.Null(request.Parse(Frame(0xB0, 0x03, 0xC8, 0x03, 0, 0)));
    }

    [Fact]
    public void HecateG3MaxSeparatesTheOfflineFrameFromZeroPercent()
    {
        var request = UsbVendorBatteryProtocol.Requests(0x35BB, 0xA226).Single();
        Assert.Equal(new byte[] { 0xD0, 0x35, 0xBB, 0x00 }, request.Report[..4]);
        Assert.Equal(64, request.Report.Length);
        Assert.Equal(4, request.Attempts);
        byte[] Frame(params byte[] head) => [.. head, .. new byte[64 - head.Length]];
        // Real replies: 0x60 = 96 %, 0x5E = 94 % after power-on, and the offline frame.
        Assert.Equal(new UsbHeadsetReading(Level: 96, IsConnected: true), request.Parse(Frame(0xD0, 0x35, 0xBB, 0x0A, 0, 1, 0x60)));
        Assert.Equal(new UsbHeadsetReading(Level: 94, IsConnected: true), request.Parse(Frame(0xD0, 0x35, 0xBB, 0x0A, 0, 1, 0x5E)));
        Assert.Equal(new UsbHeadsetReading(IsConnected: false), request.Parse(Frame(0xD0, 0x35, 0xBB, 0x0A, 0, 0, 0)));
        // Device information (07), acknowledgement (01) and LED (ED) frames are not the battery.
        Assert.Null(request.Parse(Frame(0xD0, 0x35, 0xBB, 0x07, 0x02, 0x6C, 0x16, 0x29, 0x65, 0x78, 0x12)));
        Assert.Null(request.Parse(Frame(0xD0, 0x35, 0xBB, 0x01, 0x05)));
        Assert.Null(request.Parse(Frame(0xED, 0x35, 0xBB, 0x09, 0, 0xFF, 0, 0xFF)));
        Assert.Null(request.Parse(Frame(0xD0, 0x35, 0xBB, 0x0A, 0, 2, 50)));
        Assert.Null(request.Parse(Frame(0xD0, 0x35, 0xBB, 0x0A, 0, 1, 101)));
        Assert.Null(request.Parse(Frame(0xD0, 0x35, 0xBB, 0x0A, 0, 1, 50)[..63]));
    }

    [Fact]
    public void JblQuantum910OnlyReadsWhatTheHeadsetPushes()
    {
        Assert.Equal(UsbQueryKind.Passive, UsbVendorBatteryProtocol.Kind(0x0ECB, 0x2088));
        Assert.Throws<ArgumentException>(() => UsbVendorBatteryProtocol.Query(0x0ECB, 0x2088));
        Assert.Equal(new UsbHeadsetReading(Level: 95, IsConnected: true), UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2088, [0x08, 95, 0]));
        Assert.Equal(new UsbHeadsetReading(IsConnected: false), UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2088, [0x09, 0]));
        Assert.Equal(new UsbHeadsetReading(IsConnected: true), UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2088, [0x09, 1]));
        Assert.Null(UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2088, [0x2F, 1]));
        Assert.Null(UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2088, [0x08, 101]));
        Assert.Null(UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2089, [0x08, 95]));
    }

    [Fact]
    public async Task RequestsStopAtTheFirstUnansweredOrOfflineReply()
    {
        var link = new FakeLink(r => r[5] == 0x06 ? [0x0C, 2, 3, 1, 0, 0x06, 62, .. new byte[57]] : [0x0C, 2, 3, 1, 0, 0x48, 1, .. new byte[57]]);
        var reading = await UsbVendorBattery.RequestAsync(UsbVendorBatteryProtocol.Requests(0x03F0, 0x06BE), link.SendAsync, link.ReceiveAsync, CancellationToken.None);
        Assert.Equal(new UsbHeadsetReading(62, true), reading);
        Assert.Equal(2, link.Sent.Count);

        var silent = new FakeLink(_ => null);
        Assert.True((await UsbVendorBattery.RequestAsync(UsbVendorBatteryProtocol.Requests(0x03F0, 0x06BE), silent.SendAsync, silent.ReceiveAsync, CancellationToken.None)).IsEmpty);
        Assert.Single(silent.Sent);

        var off = new FakeLink(_ => [0xD0, 0x35, 0xBB, 0x0A, 0, 0, 0, .. new byte[57]]);
        Assert.Equal(new UsbHeadsetReading(IsConnected: false),
            await UsbVendorBattery.RequestAsync(UsbVendorBatteryProtocol.Requests(0x35BB, 0xA226), off.SendAsync, off.ReceiveAsync, CancellationToken.None));
        Assert.Single(off.Sent);
    }

    [Fact]
    public async Task HecateRetriesFourTimesBeforeGivingUp()
    {
        var silent = new FakeLink(_ => null);
        Assert.True((await UsbVendorBattery.RequestAsync(UsbVendorBatteryProtocol.Requests(0x35BB, 0xA226), silent.SendAsync, silent.ReceiveAsync, CancellationToken.None)).IsEmpty);
        Assert.Equal(4, silent.Sent.Count);
    }

    [Theory]
    [InlineData(0x0555)]
    [InlineData(0x053A)]
    public async Task PaRemoteReadsBatteryAndCableAndAlwaysReleasesRemoteMode(int product)
    {
        ushort p = (ushort)product;
        bool ys = p == 0x0555;
        byte[]? Answer(byte[] request)
        {
            int at = ys ? 5 : 3;
            if (request[at + 2] != 0x08) return null;
            byte command = request[at + 5];
            var reply = new byte[64];
            reply[0] = ys ? (byte)2 : (byte)1;
            if (!ys) reply[1] = 0x80;
            int pi = ys ? 2 : 3;
            reply[pi] = 0x50; reply[pi + 1] = 0x49; reply[pi + 2] = 8;
            reply[pi + 10] = command; reply[pi + 11] = 1; reply[pi + 12] = 1; reply[pi + 13] = command == 0x21 ? (byte)42 : (byte)1;
            return reply;
        }
        var link = new FakeLink(Answer);
        Assert.Equal(new UsbHeadsetReading(42, true), await UsbVendorBattery.PaRemoteAsync(p, link.SendAsync, link.ReceiveAsync, CancellationToken.None));
        Assert.Equal(UsbVendorBatteryProtocol.Remote(p, false), link.Sent[^1]);

        var off = new FakeLink(_ => null);
        Assert.True((await UsbVendorBattery.PaRemoteAsync(p, off.SendAsync, off.ReceiveAsync, CancellationToken.None)).IsEmpty);
        // Wake, three attempts of remote + battery query, no cable query, then remote off.
        Assert.Equal(8, off.Sent.Count);
        Assert.DoesNotContain(off.Sent, r => r.SequenceEqual(UsbVendorBatteryProtocol.Query(0x1532, p, 0x2A)));
        Assert.Equal(UsbVendorBatteryProtocol.Remote(p, false), off.Sent[^1]);
    }

    [Fact]
    public async Task PassiveReceiversAreOnlyListenedTo()
    {
        var input = Channel.CreateUnbounded<byte[]>();
        input.Writer.TryWrite([0x2F, 1]);
        input.Writer.TryWrite([0x09, 1]);
        input.Writer.TryWrite([0x08, 77]);
        var reading = await UsbVendorBattery.ListenAsync(f => UsbVendorBatteryProtocol.ReadPushed(0x0ECB, 0x2088, f),
            t => input.Reader.ReadAsync(t).AsTask(), CancellationToken.None, 100);
        Assert.Equal(new UsbHeadsetReading(77, null, true), reading);
    }

    [Theory]
    [InlineData("Speakers (MCHOSE V9 Pro)", "mchose-usb-v9-pro")]
    [InlineData("MCHOSE X9", "mchose-usb-x9")]
    [InlineData("Razer Kraken V4 Pro", "razer-usb-kraken-v4-pro")]
    [InlineData("Razer BlackShark V2 HyperSpeed", "razer-usb-blackshark-v2-hyperspeed")]
    [InlineData("Razer BlackShark V3 Pro", "razer-usb-blackshark-v3-pro")]
    [InlineData("Headset (HyperX Cloud III S Wireless)", "hyperx-usb-cloud-iii-s")]
    [InlineData("Speakers (Arctis Nova 7)", "steelseries-usb-arctis-nova-7")]
    [InlineData("PRO X 2 LIGHTSPEED", "logitech-usb-g-pro-x-2")]
    public void SpecificModelsWinOverShorterNames(string name, string id)
    {
        var registry = new DeviceProfileRegistry(UsbHeadsetProfiles.Create());
        var identity = new DeviceIdentity("test", name, new BluetoothAddress(0)) { Transport = DeviceTransport.Usb };
        Assert.Equal(id, registry.Match(identity)?.ModelId);
        Assert.Null(registry.Match(identity with { Transport = DeviceTransport.Bluetooth }));
    }

    [Fact]
    public void GenericBlacksharkNameRequiresHardwareToDistinguishYear()
    {
        var registry = new DeviceProfileRegistry(UsbHeadsetProfiles.Create());
        foreach (var name in new[] { "Razer Kraken V4 X", "MCHOSE X9 Pro", "Razer Barracuda X Chroma", "Razer BlackShark V2 Pro", "Arctis Nova 7X", "HyperX Cloud Alpha 2 Pro" })
            Assert.Null(registry.Match(new DeviceIdentity("test", name, new BluetoothAddress(0)) { Transport = DeviceTransport.Usb }));
        Assert.Equal("razer-usb-blackshark-v2-pro-2023", UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_0555&MI_03"])?.ModelId);
        Assert.Equal("mchose-usb-x9", UsbHeadsetProfiles.MatchHardware([@"USB\VID_3837&PID_6045"])?.ModelId);
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_1532&PID_05550"]));
    }

    [Theory]
    [InlineData(@"USB\VID_291D&PID_385D&MI_00", "mchose-usb-v9-pro")]
    [InlineData(@"USB\VID_1532&PID_053A&MI_03", "razer-usb-barracuda-pro")]
    [InlineData(@"USB\VID_1532&PID_053C", "razer-usb-barracuda")]
    [InlineData(@"USB\VID_1532&PID_057A&MI_05", "razer-usb-blackshark-v3")]
    [InlineData(@"USB\VID_1532&PID_0577&MI_05", "razer-usb-blackshark-v3-pro")]
    [InlineData(@"USB\VID_03F0&PID_08BE&MI_02", "hyperx-usb-cloud-alpha-2")]
    [InlineData(@"USB\VID_03F0&PID_06BE&MI_03", "hyperx-usb-cloud-iii-s")]
    [InlineData(@"USB\VID_1038&PID_22A1&MI_03", "steelseries-usb-arctis-nova-7")]
    [InlineData(@"USB\VID_35BB&PID_A226&MI_03", "edifier-usb-g3-max")]
    [InlineData(@"USB\VID_0ECB&PID_2088&MI_05", "jbl-usb-quantum-910")]
    [InlineData(@"USB\VID_046D&PID_0AF7&MI_03", "logitech-usb-g-pro-x-2")]
    public void ExactReceiverIdsNameTheHeadset(string hardwareId, string model)
    {
        Assert.Equal(model, UsbHeadsetProfiles.MatchHardware([hardwareId])?.ModelId);
        Assert.False(new DeviceResourceKey(model, null, null).UsesOplusId);
        // The chat half of the HyperX station and other Nova 7 variants are not claimed.
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_03F0&PID_0ABE"]));
        Assert.Null(UsbHeadsetProfiles.MatchHardware([@"USB\VID_1038&PID_2202"]));
    }

    private sealed class FakeLink(Func<byte[], byte[]?> answer)
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Sent { get; } = [];
        public Task<byte[]> ReceiveAsync(CancellationToken ct) => _input.Reader.ReadAsync(ct).AsTask();
        public Task SendAsync(byte[] report, CancellationToken ct)
        {
            Sent.Add(report);
            if (answer(report) is { } reply) _input.Writer.TryWrite(reply);
            return Task.CompletedTask;
        }
    }
}
