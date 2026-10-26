// SPDX-License-Identifier: GPL-3.0-only
// Expected frames follow nightdevx/barracuda-x-battery barracuda.py (MIT, Copyright (c) 2026 nightdijital)
// for 1532:0550, and AbdullahSAhmad/blackshark-control plus the OpenRazer #2862 driver (GPL-2.0-or-later)
// for 1532:0555. Reply layouts follow the captured 0550 frames replayed by nightdevx's self-test.
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Protocol.Brands.Usb;

namespace EncoDeck.Tests.Protocol;

public sealed class RazerPaSettingsTests
{
    [Fact]
    public void BarracudaXFramesUseTheYsTechEnvelopeWithCrc()
    {
        byte[] get = RazerPaSettings.Get(0x0550, 5, RazerPaSetting.EqPreset);
        Assert.Equal(new byte[] { 2, 0x80, 8 }, get[..3]);
        Assert.Equal(Convert.FromHexString("5041080503130000"), get[5..13]);
        byte[] set = RazerPaSettings.Set(0x0550, 6, RazerPaSetting.CustomEq, [5, 5, 6, 7, 5, 4, 5, 5, 5, 10]);
        Assert.Equal(Convert.FromHexString("504108060D95000A0505060705040505050A"), set[5..23]);
        ushort crc = (ushort)(set[3] | set[4] << 8);
        set[3] = set[4] = 0;
        Assert.Equal(BarracudaXProtocol.Crc16Xmodem(set), crc);
        Assert.Equal(Convert.FromHexString("50410E0702E100"), RazerPaSettings.Remote(0x0550, 7, false)[5..12]);
        Assert.Throws<ArgumentOutOfRangeException>(() => RazerPaSettings.Get(0x0550, 0, RazerPaSetting.AutoOff));
    }

    [Fact]
    public void BlackSharkFramesMatchTheDriverByteForByte()
    {
        Assert.Equal(Convert.FromHexString("02800800005041080003180000"), RazerPaSettings.Get(0x0555, 9, RazerPaSetting.Sidetone)[..13]);
        Assert.Equal(Convert.FromHexString("0280090000504108000498000101"), RazerPaSettings.Set(0x0555, 9, RazerPaSetting.Sidetone, [1])[..14]);
        Assert.Equal(Convert.FromHexString("028009000050410800049900010A"), RazerPaSettings.Set(0x0555, 0, RazerPaSetting.SidetoneLevel, [10])[..14]);
        Assert.Equal(Convert.FromHexString("02800900005041080004AC00011E"), RazerPaSettings.Set(0x0555, 0, RazerPaSetting.AutoOff, [30])[..14]);
        Assert.Equal(Convert.FromHexString("028008000050410800031E0000"), RazerPaSettings.PrepareEq(0)[..13]);
        Assert.Equal(Convert.FromHexString("028009000050410800049D000102"), RazerPaSettings.EqFamily(0, 0xFA)[..14]);
        Assert.Equal(Convert.FromHexString("028009000050410800049D000101"), RazerPaSettings.EqFamily(0, 0xFF)[..14]);
        Assert.Equal(UsbVendorBatteryProtocol.Remote(true), RazerPaSettings.Remote(0x0555, 0, true));
    }

    [Theory]
    [InlineData(0x0550, RazerPaSetting.EqPreset, new byte[] { 0x08 }, true)]
    [InlineData(0x0550, RazerPaSetting.EqPreset, new byte[] { 0x01 }, false)]
    [InlineData(0x0550, RazerPaSetting.AutoOff, new byte[] { 15 }, true)]
    [InlineData(0x0550, RazerPaSetting.AutoOff, new byte[] { 7 }, false)]
    [InlineData(0x0550, RazerPaSetting.DoNotDisturb, new byte[] { 2 }, false)]
    [InlineData(0x0550, RazerPaSetting.CustomEq, new byte[] { 5, 5, 5, 5, 5, 5, 5, 5, 5 }, false)]
    [InlineData(0x0550, RazerPaSetting.CustomEq, new byte[] { 11, 5, 5, 5, 5, 5, 5, 5, 5, 5 }, false)]
    [InlineData(0x0550, RazerPaSetting.Sidetone, new byte[] { 1 }, false)]
    [InlineData(0x0555, RazerPaSetting.EqPreset, new byte[] { 0xFC }, true)]
    [InlineData(0x0555, RazerPaSetting.EqPreset, new byte[] { 0x00 }, false)]
    [InlineData(0x0555, RazerPaSetting.CustomEq, new byte[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }, false)]
    [InlineData(0x0555, RazerPaSetting.SidetoneLevel, new byte[] { 0 }, false)]
    [InlineData(0x0555, RazerPaSetting.SidetoneLevel, new byte[] { 11 }, false)]
    [InlineData(0x0555, RazerPaSetting.AutoOff, new byte[] { 0 }, true)]
    [InlineData(0x0555, RazerPaSetting.AutoOff, new byte[] { 10 }, false)]
    [InlineData(0x0552, RazerPaSetting.DoNotDisturb, new byte[] { 1 }, false)]
    [InlineData(0x0528, RazerPaSetting.AutoOff, new byte[] { 15 }, false)]
    public void OnlyConfirmedSettingsAndValuesAreAccepted(int product, RazerPaSetting setting, byte[] values, bool expected) =>
        Assert.Equal(expected, RazerPaSettings.IsValid((ushort)product, setting, values));

    [Fact]
    public void GetRepliesAreReadButUnsolicitedReportsAreNot()
    {
        byte[] reply = Convert.FromHexString("504908F600000000040013010108");
        Assert.Equal(new byte[] { 0x08 }, RazerPaSettings.Values(reply, RazerPaSetting.EqPreset));
        Assert.Null(RazerPaSettings.Values(reply, RazerPaSetting.CustomEq));
        reply[11] = 2; Assert.Null(RazerPaSettings.Values(reply, RazerPaSetting.EqPreset));
        reply[11] = 1; reply[12] = 5; Assert.Null(RazerPaSettings.Values(reply, RazerPaSetting.EqPreset));
        Assert.True(RazerPaSettings.IsSetReply(Convert.FromHexString("504908F600000000030093010000"), RazerPaSetting.EqPreset));
    }

    [Fact]
    public async Task BarracudaXWriteIsConfirmedByReadBackAndReleasesRemoteMode()
    {
        var headset = new Headset(0x0550) { [RazerPaSetting.EqPreset] = [0x07] };
        Assert.True(await UsbHeadsetSettings.WriteReportsAsync(0x0550, RazerPaSetting.EqPreset, [0x08], headset.SendAsync, headset.ReceiveAsync, CancellationToken.None));
        Assert.Equal(new byte[] { 0xE1, 0x93, 0x13, 0xE1 }, headset.Commands);
        Assert.Equal(new byte[] { 1, 0 }, headset.Routes);
        Assert.Equal(new byte[] { 0x08 }, headset[RazerPaSetting.EqPreset]);
    }

    [Fact]
    public async Task IgnoredWriteFailsAndInvalidValuesAreNeverSent()
    {
        var ignoring = new Headset(0x0550) { Ignore = true, [RazerPaSetting.AutoOff] = [15] };
        Assert.False(await UsbHeadsetSettings.WriteReportsAsync(0x0550, RazerPaSetting.AutoOff, [30], ignoring.SendAsync, ignoring.ReceiveAsync, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 0 }, ignoring.Routes);

        var invalid = new Headset(0x0550);
        Assert.False(await UsbHeadsetSettings.WriteReportsAsync(0x0550, RazerPaSetting.AutoOff, [7], invalid.SendAsync, invalid.ReceiveAsync, CancellationToken.None));
        Assert.Empty(invalid.Commands);

        var unsupported = new Headset(0x0552);
        Assert.False(await UsbHeadsetSettings.WriteReportsAsync(0x0552, RazerPaSetting.DoNotDisturb, [1], unsupported.SendAsync, unsupported.ReceiveAsync, CancellationToken.None));
        Assert.Empty(unsupported.Commands);
    }

    [Fact]
    public async Task BarracudaXWithoutRemoteAckWritesNothingButStillReleases()
    {
        var headset = new Headset(0x0550) { OmitAck = true };
        Assert.False(await UsbHeadsetSettings.WriteReportsAsync(0x0550, RazerPaSetting.DoNotDisturb, [1], headset.SendAsync, headset.ReceiveAsync, CancellationToken.None));
        Assert.Equal(new byte[] { 0xE1, 0xE1 }, headset.Commands);
        Assert.Equal(new byte[] { 1, 0 }, headset.Routes);
    }

    [Fact]
    public async Task BlackSharkSidetoneLevelIsWrittenAndReadBack()
    {
        var headset = new Headset(0x0555) { [RazerPaSetting.SidetoneLevel] = [3] };
        Assert.True(await UsbHeadsetSettings.WriteReportsAsync(0x0555, RazerPaSetting.SidetoneLevel, [7], headset.SendAsync, headset.ReceiveAsync, CancellationToken.None));
        Assert.Equal(new byte[] { 0x99, 0x19 }, headset.Commands.Where(c => c != 0xE1));
        Assert.Equal(0, headset.Routes[^1]);
        Assert.Equal(new byte[] { 7 }, headset[RazerPaSetting.SidetoneLevel]);
    }

    [Fact]
    public async Task BlackSharkPresetCrossingFamiliesIsRepeatedUntilReadBackMatches()
    {
        var headset = new Headset(0x0555) { FamilySwitchFirst = true, [RazerPaSetting.EqPreset] = [0x08] };
        Assert.True(await UsbHeadsetSettings.WriteReportsAsync(0x0555, RazerPaSetting.EqPreset, [0xFB], headset.SendAsync, headset.ReceiveAsync, CancellationToken.None));
        Assert.Equal(2, headset.Commands.Count(c => c == 0x93));
        Assert.Equal(new byte[] { 0x1E, 0x93, 0x9D, 0x13, 0x1E, 0x93, 0x9D, 0x13 }, headset.Commands.Where(c => c != 0xE1));
        Assert.Equal(0, headset.Routes[^1]);
    }

    [Fact]
    public async Task ReadsOnlySupportedSettings()
    {
        var headset = new Headset(0x0550) { [RazerPaSetting.DoNotDisturb] = [1], [RazerPaSetting.CustomEq] = [5, 5, 5, 5, 5, 5, 5, 5, 5, 5] };
        var values = await UsbHeadsetSettings.ReadReportsAsync(0x0550, [RazerPaSetting.DoNotDisturb, RazerPaSetting.CustomEq, RazerPaSetting.Sidetone],
            headset.SendAsync, headset.ReceiveAsync, CancellationToken.None);
        Assert.Equal(new byte[] { 1 }, values[RazerPaSetting.DoNotDisturb]);
        Assert.Equal(10, values[RazerPaSetting.CustomEq].Length);
        Assert.False(values.ContainsKey(RazerPaSetting.Sidetone));
        Assert.DoesNotContain((byte)0x18, headset.Commands);
    }

    /// <summary>A receiver and headset answering in the captured 0550 reply layout.</summary>
    private sealed class Headset(ushort product)
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private readonly Dictionary<RazerPaSetting, byte[]> _stored = [];
        public List<byte> Commands { get; } = [];
        public List<byte> Routes { get; } = [];
        public bool Ignore { get; init; }
        public bool OmitAck { get; init; }
        public bool FamilySwitchFirst { get; set; }
        public byte[] this[RazerPaSetting setting] { get => _stored[setting]; set => _stored[setting] = value; }
        public Task<byte[]> ReceiveAsync(CancellationToken ct) => _input.Reader.ReadAsync(ct).AsTask();

        public Task SendAsync(byte[] report, CancellationToken ct)
        {
            Assert.Equal(2, report[0]);
            byte group = report[7], sequence = report[8], length = report[9];
            byte[] body = report[10..(10 + length)];
            Commands.Add(body[0]);
            if (body[0] == 0xE1)
            {
                Routes.Add(body[1]);
                if (product == 0x0550 && !OmitAck) Reply([0x50, 0x49, 1, 0, 0, 0, 0, 0, 3, 0, 0x0E, (byte)(sequence | 0x80), 0]);
                return Task.CompletedTask;
            }
            Assert.Equal(0x08, group);
            var setting = (RazerPaSetting)(body[0] & 0x7F);
            if ((body[0] & 0x80) != 0 && body[0] != 0x9D)
            {
                byte[] values = body[3..];
                if (!Ignore)
                    _stored[setting] = FamilySwitchFirst && setting == RazerPaSetting.EqPreset && (values[0] >= 0xFA) != (_stored[setting][0] >= 0xFA)
                        ? [0xFE] : values;
                if (setting == RazerPaSetting.EqPreset && _stored[setting][0] == 0xFE) FamilySwitchFirst = false;
                Reply([0x50, 0x49, 8, 0, 0, 0, 0, 0, 3, 0, body[0], 1, 0]);
            }
            else if (body[0] == 0x1E) Reply([0x50, 0x49, 8, 0, 0, 0, 0, 0, 4, 0, 0x1E, 1, 1, 1]);
            else if (body[0] != 0x9D && _stored.TryGetValue(setting, out var stored))
                Reply([0x50, 0x49, 8, 0, 0, 0, 0, 0, (byte)(3 + stored.Length), 0, body[0], 1, (byte)stored.Length, .. stored]);
            return Task.CompletedTask;
        }

        private void Reply(byte[] frame)
        {
            var report = new byte[64];
            report[0] = 2; report[1] = (byte)frame.Length;
            frame.CopyTo(report, 2);
            _input.Writer.TryWrite(report);
        }
    }
}
