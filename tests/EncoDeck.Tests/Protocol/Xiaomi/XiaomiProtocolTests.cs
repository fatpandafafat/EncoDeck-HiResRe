// SPDX-License-Identifier: GPL-3.0-only
// Wire fixtures are real-device captures published by community projects:
// - OpenXia (https://github.com/thetriu/OpenXia, MIT, Copyright (c) 2026 OpenXia contributors; f2650cf):
//   Redmi Buds 6 Play device-info request/response, auth vectors, EQ / find / low-latency / gesture writes.
// - BudsLink PR #110 (https://github.com/maniacx/BudsLink/pull/110, GPL-3.0, tester digoxi): REDMI Buds 6 Pro
//   auth exchange, device info, gestures and custom curve.
// - redmi-buds-bar by robin-liquidium (GPL-3.0, e4337d2): REDMI Buds 8 Pro noise read / write / readback.
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Xiaomi;

namespace EncoDeck.Tests.Protocol.Xiaomi;

public class XiaomiProtocolTests
{
    private static byte[] Hex(params string[] parts) => XiaomiFakeEarbuds.Hex(parts);

    public static TheoryData<byte, byte, byte, string, string> Fixtures => new()
    {
        // OpenXia frame 595: device info with every record requested.
        { 0xC4, 0x02, 0x02, "FFFFFFFF", "FEDCBAC402000502FFFFFFFFEF" },
        // OpenXia: low latency on, EQ preset 5, ring left / both, single-tap left = none.
        { 0xC4, 0xF2, 0x18, "03002F01", "FEDCBAC4F200051803002F01EF" },
        { 0xC4, 0xF2, 0x0E, "03000705", "FEDCBAC4F200050E03000705EF" },
        { 0xC4, 0xF2, 0x1C, "04000901 01", "FEDCBAC4F200061C0400090101EF" },
        { 0xC4, 0xF2, 0x1F, "04000901 03", "FEDCBAC4F200061F0400090103EF" },
        // robin-liquidium: REDMI Buds 8 Pro noise read and its answer (status 00, sequence 02).
        { 0xC0, 0xF3, 0x02, "000B", "FEDCBAC0F30003 02000BEF" },
        { 0x00, 0xF3, 0x00, "02 04000B0113", "FEDCBA00F30007 00 02 04000B0113 EF" },
        // BudsLink PR #110: auth verdict and its acknowledgement.
        { 0xC4, 0x51, 0x01, "0100", "FEDCBAC45100030101 00EF" },
        { 0x04, 0x51, 0x00, "01 01", "FEDCBA045100030001 01EF" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Frame_EncodesAndDecodesAcrossSplits(byte kind, byte op, byte seq, string payloadHex, string hex)
    {
        var payload = Hex(payloadHex);
        var wire = Hex(hex);
        var frame = new XiaomiFrame(kind, op, seq, payload);
        Assert.Equal(wire, frame.Encode());

        for (int split = 0; split <= wire.Length; split++)
        {
            var decoder = new XiaomiFrameDecoder();
            var a = decoder.Feed([0x00, 0x01, 0x02, .. wire.AsSpan(0, split)]);
            var b = decoder.Feed([.. wire.AsSpan(split), .. wire]);
            var all = a.Concat(b).ToList();
            Assert.Equal(2, all.Count);
            Assert.All(all, f =>
            {
                Assert.Equal(kind, f.Kind);
                Assert.Equal(op, f.OpCode);
                Assert.Equal(seq, f.Sequence);
                Assert.Equal(payload, f.Payload);
            });
        }
    }

    [Fact]
    public void Response_StatusAndSequenceAreSeparated()
    {
        // robin-liquidium: the noise answer carries status 00 and the request's sequence 02.
        var frame = XiaomiConfigCodec.NormalizeResponse(new XiaomiFrameDecoder().Feed(Hex("FEDCBA00F3000700020400 0B0113EF")).Single())!;
        Assert.True(frame.IsResponse);
        Assert.Equal(0, frame.Status);
        Assert.Equal(2, frame.Sequence);
        Assert.True(XiaomiConfigCodec.TryParse(frame.Payload, out var values));
        Assert.Equal(new byte[] { 1, 0x13 }, values[XiaomiConfig.Noise]); // ANC, strength 19 of 0–19

        var refused = XiaomiConfigCodec.NormalizeResponse(new XiaomiFrame(4, 0xF3, 4, [9]))!;
        Assert.Equal(4, refused.Status);
        Assert.Equal(9, refused.Sequence);
        Assert.Empty(refused.Payload);
        Assert.Null(XiaomiConfigCodec.NormalizeResponse(new XiaomiFrame(4, 0xF2, 0, [])));

        var push = new XiaomiFrameDecoder().Feed(Hex("FEDCBA87F40006070400 0B0201EF")).Single();
        Assert.False(push.IsResponse);
        Assert.False(push.NeedsReply); // 0x87 pushes are informational
        Assert.Same(push, XiaomiConfigCodec.NormalizeResponse(push));
    }

    [Fact]
    public void Decoder_SkipsFalseHeaderWithBadFooter()
    {
        var decoder = new XiaomiFrameDecoder();
        var good = new XiaomiFrame(0xC4, 0x02, 0x0B, [0xFF, 0xFF, 0xFF, 0xFF]).Encode();
        var frames = decoder.Feed([0xFE, 0xDC, 0xBA, 0xC4, 0x02, 0x00, 0x02, 0x00, 0x00, 0x11, .. good]);
        Assert.Equal(good, Assert.Single(frames).Encode());
    }

    [Fact]
    public void TargetInfo_RedmiBuds6Play_OpenXiaCapture()
    {
        // OpenXia frame 619 after status 00 / sequence 02.
        var payload = Hex("12005265646D692042756473203620506C6179", "050120222022", "02020A", "05032717509B", "020401", "020500", "03060202", "0407E4B264", "020D01");
        Assert.True(XiaomiProtocol.TryParseTargetInfo(payload, out var info));
        Assert.Equal("Redmi Buds 6 Play", info.Name);
        Assert.Equal("2.0.2.2", info.Firmware);
        Assert.Equal((0x2717, 0x509B), (info.VendorId, info.ProductId));
        Assert.Equal(new BatteryReading?[] { new(100, true), new(50, true), new(100, false) }, info.Battery);
    }

    [Fact]
    public void TargetInfo_RedmiBuds6Pro_BudsLinkCapture()
    {
        Assert.True(XiaomiProtocol.TryParseTargetInfo(XiaomiFakeEarbuds.SixProTargetInfo, out var info));
        Assert.Equal("Redmi Buds 6 Pro", info.Name);
        Assert.Equal("1.3.9.9", info.Firmware);
        Assert.Equal((0x2717, 0x509E), (info.VendorId, info.ProductId));
        Assert.Equal(new BatteryReading?[] { new(95, false), new(100, false), null }, info.Battery);
    }

    [Theory]
    [InlineData(0xE4, 100, true)]
    [InlineData(0xC6, 70, true)]
    [InlineData(0x64, 100, false)]
    [InlineData(0x5F, 95, false)]
    public void Battery_LevelAndChargingBit(byte value, int level, bool charging) =>
        Assert.Equal(new BatteryReading(level, charging), XiaomiProtocol.DecodeBattery(value));

    [Fact]
    public void Battery_MissingAndOutOfRangeAreUnknown()
    {
        Assert.Null(XiaomiProtocol.DecodeBattery(0xFF));
        Assert.Null(XiaomiProtocol.DecodeBattery(0x7F)); // 127 % is not a level
        Assert.False(XiaomiProtocol.TryParseTargetInfo([0x12, 0x00, 0x01], out _)); // truncated record only
    }

    [Theory]
    // OpenXia (Redmi Buds 6 Play): the earbuds' answer to the app, and the app's answer to the earbuds.
    [InlineData("67C6697351FF4AEC29CDBAABF2FBE346", "12953FFA34F0B875CD3B6850B974856C")]
    [InlineData("0C69EAEA3A760B4C2E2234E9A5E4595C", "A9B389A0BE9DCD42D768435F8553C7BF")]
    // BudsLink PR #110 (REDMI Buds 6 Pro): both rounds, accepted by the earbuds.
    [InlineData("210AF0F8A452B486EDD415642A0C9688", "B5FD4CB77CC5DEBE1305FAE1616E6983")]
    [InlineData("A64D81FC411ACDDDDE9F9AE4A58C7630", "B76DE0A0A7432361EA57008D06923C68")]
    public void Auth_MatchesRealDeviceCaptures(string challenge, string answer)
    {
        Assert.Equal(Hex(answer), XiaomiAuth.GetEncryptedAuthCheckData(Hex(challenge)));
        Assert.True(XiaomiAuth.VerifyAuthResponse(Hex(challenge), Hex(answer)));
        Assert.False(XiaomiAuth.VerifyAuthResponse(Hex(challenge), new byte[16]));
    }

    [Fact]
    public void Auth_PayloadsRoundTrip()
    {
        var random = XiaomiAuth.CreateRandomFactor();
        var payload = XiaomiAuth.BuildAuthCheckPayload(random);
        Assert.Equal(17, payload.Length);
        Assert.Equal(1, payload[0]);
        Assert.True(XiaomiAuth.TryParseAuthCheckResponse(payload, out var version, out var result));
        Assert.Equal(1, version);
        Assert.Equal(random, result);
        Assert.Throws<ArgumentException>(() => XiaomiAuth.GetEncryptedAuthCheckData([0]));
    }

    [Fact]
    public void Gestures_ParseCapturesAndBuildSingleSideWrites()
    {
        // BudsLink PR #110 (6 Pro): single none/none, double play/play, triple next/next, hold noise/noise, slide volume/volume.
        Assert.True(XiaomiGestures.TryParse(Hex("040808010101020303030606050B0B"), out var groups));
        var keys = XiaomiGestures.ToKeyFunctions(groups);
        Assert.Contains(new KeyFunction(EarSide.Left, 0, GestureAction.SingleSqueeze, XiaomiGestures.None), keys);
        Assert.Contains(new KeyFunction(EarSide.Right, 0, GestureAction.TripleSqueeze, XiaomiGestures.Next), keys);
        Assert.Contains(new KeyFunction(EarSide.Left, 0, GestureAction.SqueezeAndHold, XiaomiGestures.NoiseControl), keys);
        Assert.Contains(new KeyFunction(EarSide.Right, 0, GestureAction.Slide, XiaomiGestures.Volume), keys);
        Assert.Equal(10, keys.Count);

        // OpenXia (6 Play) read with an unknown kind 9 appended: unknown kinds are kept out of the UI.
        Assert.True(XiaomiGestures.TryParse(Hex("040303010808020808030800", "090102"), out var playGroups));
        Assert.Equal(8, XiaomiGestures.ToKeyFunctions(playGroups).Count);
        Assert.False(XiaomiGestures.TryParse(Hex("0403"), out _));
        Assert.False(XiaomiGestures.TryParse(Hex("040303040808"), out _)); // duplicated kind

        // OpenXia writes: single tap left = none; triple tap right = previous (0xFF keeps the other earbud).
        Assert.Equal(Hex("0408FF"), XiaomiGestures.WriteValue(XiaomiGestures.KindSingle, EarSide.Left, XiaomiGestures.None));
        Assert.Equal(Hex("02FF02"), XiaomiGestures.WriteValue(XiaomiGestures.KindTriple, EarSide.Right, XiaomiGestures.Previous));
        Assert.Equal(Hex("05000204 08FF"), XiaomiConfigCodec.WritePayload(XiaomiConfig.Gestures, XiaomiGestures.WriteValue(4, EarSide.Left, 8)));
        Assert.DoesNotContain(XiaomiGestures.NoiseControl, XiaomiGestures.Functions(GestureAction.SqueezeAndHold, noiseControl: false));
    }

    [Fact]
    public void Gestures_ShareTheBrandGestureRows()
    {
        // Both earbuds × five gestures; each slot matches the key functions the parser reports.
        var slots = XiaomiGestures.Slots;
        Assert.Equal(10, slots.Count);
        var hold = Assert.Single(slots, s => s.Side == EarSide.Left && s.Action == GestureAction.SqueezeAndHold);
        Assert.True(hold.Matches(new KeyFunction(EarSide.Left, 0, GestureAction.SqueezeAndHold, XiaomiGestures.NoiseControl)));
        // Noise control on hold is listed but needs confirmed noise control; the other choices need nothing.
        Assert.Equal(EncoDeck.Core.Capabilities.DeviceCapability.NoiseCancellation,
            Assert.Single(hold.Choices, c => c.Function == XiaomiGestures.NoiseControl).Requires);
        Assert.All(slots.SelectMany(s => s.Choices).Where(c => c.Function != XiaomiGestures.NoiseControl), c => Assert.Null(c.Requires));
    }

    [Fact]
    public void CustomCurve_ParsesCaptureAndBuildsSaveWrite()
    {
        Assert.True(XiaomiCustomCurve.TryParse(XiaomiFakeEarbuds.SixProCustomCurve, out var curve));
        Assert.Equal((6, -6), (curve.MaxGain, curve.MinGain));
        Assert.Equal(new[] { 62, 125, 250, 500, 1000, 2000, 4000, 8000, 12000, 16000 }, curve.Bands.Select(b => b.FrequencyHz));
        Assert.All(curve.Bands, b => Assert.Equal(0, b.Gain));

        var bands = curve.Bands.Select((b, i) => b with { Gain = i - 5 }).ToArray();
        var save = XiaomiCustomCurve.SaveValue(bands);
        Assert.Equal(Hex("01 0A 01 01 01 00 0A", "003E85"), save[..10]); // −5 dB is sign-magnitude 0x85
        Assert.Equal(-6, XiaomiCustomCurve.DecodeGain(0x86));
        Assert.Equal(0x84, XiaomiCustomCurve.EncodeGain(-4));
        Assert.False(XiaomiCustomCurve.TryParse(Hex("01 00 06 06 00 00 01", "003E07"), out _)); // +7 dB exceeds the range
        Assert.False(XiaomiCustomCurve.TryParse(Hex("01 00 06 06 00 00 02", "003E00"), out _)); // band count mismatch
    }

    [Theory]
    [InlineData("Xiaomi Buds 5 Pro", "xiaomi-earbuds")]
    [InlineData("Mi True Wireless Earphones 2", "xiaomi-earbuds")]
    [InlineData("FlipBuds Pro", "xiaomi-earbuds")]
    [InlineData("my Redmi Buds", "redmi-earbuds")]
    [InlineData("Redmi Buds 6 Pro", "redmi-earbuds")]
    [InlineData("AirPods Pro", null)]
    [InlineData("Soundcore Liberty Air", null)]
    [InlineData("OPPO Enco X4", null)]
    [InlineData("Keyboard", null)]
    public void Profiles_MatchByName(string name, string? modelId)
    {
        var identity = new DeviceIdentity("id", name, new BluetoothAddress(0x112233445566));
        var best = XiaomiProfiles.CreateBuiltIn()
            .Select(p => (p, s: p.MatchScore(identity)))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .Select(x => x.p.ModelId)
            .FirstOrDefault();
        Assert.Equal(modelId, best);
    }

    [Fact]
    public void Profiles_AreGenericXiaomiOnFastConnectThenXiaoAiSpp()
    {
        foreach (var profile in XiaomiProfiles.CreateBuiltIn().Cast<IControllableProfile>())
        {
            Assert.Equal("xiaomi", profile.Brand);
            Assert.True(profile.IsGeneric);
            Assert.Equal(profile.ModelId, profile.ImageKey);
            Assert.Equal([new Guid("0000FD2D-0000-1000-8000-00805F9B34FB"), new Guid("00001101-0000-1000-8000-008584D01810")], profile.ControlServiceUuids);
        }
    }
}
