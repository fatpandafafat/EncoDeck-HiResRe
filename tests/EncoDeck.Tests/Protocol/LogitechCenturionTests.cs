// SPDX-License-Identifier: GPL-3.0-only
// Frame vectors and the fake receiver follow HeyOkay/HaloBattery tests/test_logitech_centurion.py (MIT,
// Copyright (c) 2026 Halo Battery contributors): Solaar's dump of a real PRO X 2 LIGHTSPEED puts the bridge
// at receiver index 3 and battery feature 0104 at headset index 4.
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;
using EncoDeck.Protocol.Brands.Usb;
using static EncoDeck.Protocol.Brands.Usb.LogitechCenturionProtocol;

namespace EncoDeck.Tests.Protocol;

public sealed class LogitechCenturionTests
{
    [Fact]
    public void FramesMatchThePublishedLayout()
    {
        Assert.Equal(new byte[] { 0x51, 0x05, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }, Direct(0, Function.GetFeature, [0x00, 0x01])[..8]);
        Assert.Equal(64, Direct(0, Function.Count).Length);
        // HeadsetControl's fixed legacy frame is this one with software id 0A instead of 01.
        Assert.Equal(new byte[] { 0x51, 0x08, 0x00, 0x03, 0x11, 0x00, 0x03, 0x00, 0x04, 0x01 }, Bridged(3, 4, Function.BatteryState)[..10]);
        Assert.Null(Payload([0x11, 0xFF, 0x00]));
        Assert.Null(Payload([0x51, 0x40, 0, 0]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, null)]
    public async Task ReadsBatteryThroughTheBridgeAndCachesDiscovery(int state, bool? charging)
    {
        var receiver = new Receiver { State = (byte)state };
        string key = Guid.NewGuid().ToString();
        var reading = await LogitechCenturionBattery.ReadReportsAsync(key, receiver.SendAsync, receiver.ReceiveAsync, CancellationToken.None);
        Assert.Equal(new UsbHeadsetReading(80, charging, true), reading);
        int discovery = receiver.Sent.Count;
        await LogitechCenturionBattery.ReadReportsAsync(key, receiver.SendAsync, receiver.ReceiveAsync, CancellationToken.None);
        Assert.Equal(discovery + 1, receiver.Sent.Count);
    }

    [Fact]
    public async Task OnlyReadFunctionsAreSent()
    {
        var receiver = new Receiver();
        await LogitechCenturionBattery.ReadReportsAsync(Guid.NewGuid().ToString(), receiver.SendAsync, receiver.ReceiveAsync, CancellationToken.None);
        foreach (byte[] sent in receiver.Sent)
        {
            byte[] p = Payload(sent)!;
            var (table, index, function) = p[0] == 3 && p[1] == 0x11 ? (Receiver.Headset, p[5], p[6]) : (Receiver.Dongle, p[0], p[1]);
            Assert.Contains(((int)table[index], function & 0xF0), new[] { (0x0000, 0x00), (0x0001, 0x00), (0x0001, 0x10), (0x0104, 0x00) });
        }
    }

    [Fact]
    public async Task HeadsetThatOnlyGetsAcknowledgedIsOffAndIsDiscoveredAgainLater()
    {
        string key = Guid.NewGuid().ToString();
        var off = new Receiver { HeadsetOn = false };
        Assert.Equal(new UsbHeadsetReading(IsConnected: false), await LogitechCenturionBattery.ReadReportsAsync(key, off.SendAsync, off.ReceiveAsync, CancellationToken.None));
        var on = new Receiver();
        Assert.Equal(80, (await LogitechCenturionBattery.ReadReportsAsync(key, on.SendAsync, on.ReceiveAsync, CancellationToken.None)).Level);
    }

    [Fact]
    public async Task ReceiverErrorsAndHeadsetNotificationsAreNotAnswers()
    {
        var failing = new Receiver { FailRoot = true };
        Assert.True((await LogitechCenturionBattery.ReadReportsAsync(Guid.NewGuid().ToString(), failing.SendAsync, failing.ReceiveAsync, CancellationToken.None)).IsEmpty);
        Assert.Single(failing.Sent);

        var noisy = new Receiver { Level = 64, Noise = true };
        Assert.Equal(64, (await LogitechCenturionBattery.ReadReportsAsync(Guid.NewGuid().ToString(), noisy.SendAsync, noisy.ReceiveAsync, CancellationToken.None)).Level);

        var high = new Receiver { Level = 0xC8 };
        Assert.True((await LogitechCenturionBattery.ReadReportsAsync(Guid.NewGuid().ToString(), high.SendAsync, high.ReceiveAsync, CancellationToken.None)).IsEmpty);
    }

    private sealed class Receiver
    {
        public static readonly ushort[] Dongle = [0x0000, 0x0001, 0x0100, 0x0003, 0x010A];
        public static readonly ushort[] Headset = [0x0000, 0x0001, 0x0101, 0x0100, 0x0104, 0x010A, 0x0108, 0x0604, 0x0602, 0x0636];
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Sent { get; } = [];
        public byte Level { get; init; } = 80;
        public byte State { get; init; }
        public bool HeadsetOn { get; init; } = true;
        public bool FailRoot { get; init; }
        public bool Noise { get; init; }
        public Task<byte[]> ReceiveAsync(CancellationToken ct) => _input.Reader.ReadAsync(ct).AsTask();

        public Task SendAsync(byte[] report, CancellationToken ct)
        {
            Sent.Add(report);
            byte[] p = Payload(report)!;
            if (p[0] == 3 && p[1] == 0x11)
            {
                byte[] sub = p[4..(4 + (p[2] << 8 | p[3]))];
                Write([p[0], 0x11]);
                if (!HeadsetOn) return Task.CompletedTask;
                if (Noise && Headset[sub[1]] == 0x0104)
                {
                    // A notification and a reply to another function arrive before the battery answer.
                    Write([3, 0x10, 0, 4, 0xFF, sub[1], 0x01, 5]);
                    Write([3, 0x10, 0, 4, 0x00, sub[1], 0x11, 6]);
                }
                byte[]? data = Answer(Headset, sub[1], (byte)(sub[2] & 0xF0), sub[3..]);
                Write(data is null ? [3, 0x10, 0, 4, 0, 0xFF, sub[1]] : [3, 0x10, 0, (byte)(3 + data.Length), 0, sub[1], sub[2], .. data]);
            }
            else if (FailRoot && p[0] == 0) Write([0xFF, 0x00, 0x01, 0x05]);
            else
            {
                if (Noise) Write([0x07, 0x01, 0x05]);
                Write([p[0], p[1], .. Answer(Dongle, p[0], (byte)(p[1] & 0xF0), p[2..]) ?? []]);
            }
            return Task.CompletedTask;
        }

        private byte[]? Answer(ushort[] table, byte index, byte function, byte[] parameters)
        {
            if (index == 0 && function == 0) return [(byte)Math.Max(0, Array.IndexOf(table, (ushort)(parameters[0] << 8 | parameters[1])))];
            if (index == 1 && function == 0) return [(byte)table.Length];
            if (index == 1 && function == 0x10) return [parameters[0], (byte)(table[parameters[0]] >> 8), (byte)table[parameters[0]], 0];
            if (index < table.Length && table[index] == 0x0104 && function == 0) return [Level, 0, State];
            return null;
        }

        private void Write(byte[] payload)
        {
            var report = new byte[64];
            report[0] = 0x51; report[1] = (byte)(payload.Length + 1);
            payload.CopyTo(report, 3);
            _input.Writer.TryWrite(report);
        }
    }
}
