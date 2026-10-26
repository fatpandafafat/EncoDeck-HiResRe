// SPDX-License-Identifier: GPL-3.0-only
// The 1532:0550 replies use the frame layout captured by nightdevx/barracuda-x-battery (MIT, Copyright (c)
// 2026 nightdijital); the 0552 flow follows leag/razer-barracuda.
using System.Threading.Channels;
using EncoDeck.Bluetooth.Usb;

namespace EncoDeck.Tests.Protocol;

public sealed class BarracudaXSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MacronixReadsFragmentedTelemetryWithEitherLinkAckOrderAndRestoresRoute(bool dataFirst)
    {
        var receiver = new Receiver(false, dataFirst);
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, false, CancellationToken.None);
        Assert.Equal(new UsbHeadsetStatus(73, true, true), result);
        Assert.Equal(new byte[] { 0xE3, 0xE0, 0xE1, 0x21, 0x2A, 0xE1, 0xE0 }, receiver.Commands);
        Assert.Equal(new byte[] { 1, 0 }, receiver.Routes);
    }

    [Fact]
    public async Task ReplacementUsesTheTestedRemoteSessionWithoutHandshakeOrLinkQueries()
    {
        var receiver = new Receiver(true, false);
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, true, CancellationToken.None);
        Assert.Equal(new UsbHeadsetStatus(73, true, true), result);
        Assert.Equal(new byte[] { 0xE1, 0x21, 0x2A, 0xE1 }, receiver.Commands);
        Assert.Equal(new byte[] { 1, 0 }, receiver.Routes);
        Assert.DoesNotContain(receiver.Reports, r => r[1] != 0x80);
    }

    [Fact]
    public async Task ReplacementWithoutRemoteAckStillReleasesTheRoute()
    {
        var receiver = new Receiver(true, false) { OmitAck = true };
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, true, CancellationToken.None);
        Assert.Equal(new UsbHeadsetStatus(), result);
        Assert.Equal(new byte[] { 0xE1, 0xE1 }, receiver.Commands);
        Assert.Equal(new byte[] { 1, 0 }, receiver.Routes);
    }

    [Fact]
    public async Task ReplacementSilentHeadsetSkipsCableQueryAndClaimsNothing()
    {
        var receiver = new Receiver(true, false) { Silent = true };
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, true, CancellationToken.None);
        Assert.Equal(new UsbHeadsetStatus(), result);
        Assert.Equal(new byte[] { 0xE1, 0x21, 0xE1 }, receiver.Commands);
    }

    [Fact]
    public async Task ReceiverWithoutRadioLinkDoesNotQueryHeadset()
    {
        var receiver = new Receiver(false, false) { Connected = false };
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, false, CancellationToken.None);
        Assert.Equal(new UsbHeadsetStatus(IsConnected: false), result);
        Assert.Equal(new byte[] { 0xE3 }, receiver.Commands);
        Assert.Empty(receiver.Routes);
    }

    [Fact]
    public async Task LinkDataWithoutMatchingAckDoesNotEstablishConnection()
    {
        var receiver = new Receiver(false, false) { OmitAck = true };
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, false, CancellationToken.None);
        Assert.Null(result.IsConnected);
        Assert.Null(result.Level);
        Assert.Equal(new byte[] { 0xE3 }, receiver.Commands);
    }

    [Fact]
    public async Task RemoteRouteOwnedByAnotherApplicationIsLeftAlone()
    {
        var receiver = new Receiver(false, false) { RemoteOwned = true };
        var result = await BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, false, CancellationToken.None);
        Assert.True(result.IsConnected);
        Assert.Null(result.Level);
        Assert.Empty(receiver.Routes);
        Assert.Equal(new byte[] { 0xE3, 0xE0 }, receiver.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringRemoteSelectionStillRestoresLocalRoute(bool replacement)
    {
        using var stop = new CancellationTokenSource();
        var receiver = new Receiver(replacement, false) { CancelRemote = stop };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BarracudaXBattery.ReadReportsAsync(receiver.SendAsync, receiver.ReceiveAsync, replacement, stop.Token));
        Assert.Equal(new byte[] { 1, 0 }, receiver.Routes);
        Assert.Equal(replacement ? 0xE1 : 0xE0, receiver.Commands.Last());
    }

    private sealed class Receiver(bool replacement, bool dataFirst)
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        public List<byte> Commands { get; } = [];
        public List<byte> Routes { get; } = [];
        public List<byte[]> Reports { get; } = [];
        public bool Connected { get; init; } = true;
        public bool OmitAck { get; init; }
        public bool RemoteOwned { get; init; }
        public bool Silent { get; init; }
        public CancellationTokenSource? CancelRemote { get; init; }
        public Task<byte[]> ReceiveAsync(CancellationToken ct) => _input.Reader.ReadAsync(ct).AsTask();

        public Task SendAsync(byte[] report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reports.Add(report);
            if (!replacement && report[1] == 0x40)
            {
                _input.Writer.TryWrite([1, 0x40, 1, 1]);
                return Task.CompletedTask;
            }
            int offset = replacement ? 5 : 3;
            byte sequence = report[offset + 3], command = report[offset + 5];
            Commands.Add(command);
            // Same shapes as the captured 0550 acknowledgement and battery reply.
            byte[] ack = [0x50, 0x49, 1, 0xA0, 0, 0, 0, 0, 3, 0, 0x0E, (byte)(sequence | 0x80), 0];
            byte[] Link(byte cmd, byte value) => [0x50, 0x49, 0x0E, 0xB0, 0, 0, 0, 0, 2, 0, cmd, value];
            switch (command)
            {
                case 0xE3:
                    byte[] link = Link(0xE3, Connected ? (byte)1 : (byte)0);
                    // Split the combined PI messages across HID chunks, including in the middle of a frame.
                    byte[] combined = OmitAck ? link : dataFirst ? [.. link, .. ack] : [.. ack, .. link];
                    Enqueue(combined[..7]); Enqueue(combined[7..]);
                    break;
                case 0xE0:
                    Enqueue(Link(0xE0, RemoteOwned ? (byte)1 : (byte)0));
                    break;
                case 0xE1:
                    byte route = report[offset + 6];
                    Routes.Add(route);
                    if (route == 1 && CancelRemote is { } stop)
                    { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
                    if (!(replacement && OmitAck)) Enqueue(ack);
                    break;
                case 0x21:
                case 0x2A:
                    if (!Silent) Enqueue([0x50, 0x49, 8, 0xB1, 0, 0, 0, 0, 4, 0, command, 1, 1, command == 0x21 ? (byte)73 : (byte)1]);
                    break;
                default: throw new InvalidOperationException("Unexpected command");
            }
            return Task.CompletedTask;
        }

        private void Enqueue(byte[] payload)
        {
            var report = new byte[64];
            report[0] = replacement ? (byte)2 : (byte)1;
            if (!replacement) report[1] = 0x80;
            int offset = replacement ? 2 : 3;
            report[offset - 1] = (byte)payload.Length;
            payload.CopyTo(report, offset);
            _input.Writer.TryWrite(report);
        }
    }
}
