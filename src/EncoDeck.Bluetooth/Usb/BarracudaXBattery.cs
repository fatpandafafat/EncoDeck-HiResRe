// SPDX-License-Identifier: GPL-3.0-only
// The 1532:0550 session follows nightdevx/barracuda-x-battery barracuda.py (MIT, Copyright (c) 2026
// nightdijital), tested on that receiver; see THIRD_PARTY_NOTICES.md.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Foundation;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.Usb;

/// <param name="Product">Receiver PID that answered, for receivers whose headset settings can be offered.</param>
public sealed record UsbHeadsetStatus(int? Level = null, bool? IsCharging = null, bool? IsConnected = null, ushort? Product = null);

internal static class BarracudaXBattery
{
    public static async Task<UsbHeadsetStatus> ReadAsync(HidDevice device, CancellationToken ct, ILogger? logger = null)
    {
        if (!BarracudaXProtocol.IsCollection(device.VendorId, device.ProductId, device.UsagePage, device.UsageId)) return new();
        bool replacement = device.ProductId == 0x0550;
        var (receive, detach) = Listen(device);
        try
        {
            return await ReadReportsAsync(Sender(device, replacement), receive, replacement, ct, logger).ConfigureAwait(false) with { Product = device.ProductId };
        }
        finally { detach(); }
    }

    internal static (Func<CancellationToken, Task<byte[]>> Receive, Action Detach) Listen(HidDevice device)
    {
        var reports = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = false });
        TypedEventHandler<HidDevice, HidInputReportReceivedEventArgs> handler = (_, args) => reports.Writer.TryWrite(args.Report.Data.ToArray());
        device.InputReportReceived += handler;
        return (token => reports.Reader.ReadAsync(token).AsTask(), () => device.InputReportReceived -= handler);
    }

    /// <summary>
    /// 64-byte output reports. The YS-Tech bridge reports through GetInputReport whether it took the report
    /// ('O') or is busy ('F'); a busy bridge gets the same report again, up to five times.
    /// </summary>
    internal static Func<byte[], CancellationToken, Task> Sender(HidDevice device, bool replacement) => async (data, token) =>
    {
        for (int attempt = 0; ; attempt++)
        {
            if (attempt > 0) await Task.Delay(50, token).ConfigureAwait(false);
            var report = device.CreateOutputReport(data[0]);
            if (report.Data.Length != 64) throw new IOException("Unexpected Barracuda HID report length");
            report.Data = data.AsBuffer();
            if (await device.SendOutputReportAsync(report).AsTask(token).WaitAsync(TimeSpan.FromMilliseconds(800), token).ConfigureAwait(false) != 64)
                throw new IOException("Incomplete Barracuda HID report");
            if (!replacement) return;
            var status = await device.GetInputReportAsync(2).AsTask(token).WaitAsync(TimeSpan.FromMilliseconds(1200), token).ConfigureAwait(false);
            byte[] bytes = status.Data.ToArray();
            if (bytes.Length != 64 || bytes[0] != 2 || bytes[1] is not (BarracudaXProtocol.BridgeTaken or BarracudaXProtocol.BridgeBusy))
                throw new IOException("Barracuda replacement receiver did not acknowledge HID output");
            if (bytes[1] == BarracudaXProtocol.BridgeTaken) return;
            if (attempt == 4) throw new IOException("Barracuda replacement receiver stayed busy");
        }
    };

    internal static async Task<UsbHeadsetStatus> ReadReportsAsync(
        Func<byte[], CancellationToken, Task> send,
        Func<CancellationToken, Task<byte[]>> receive,
        bool replacement, CancellationToken ct, ILogger? logger = null)
    {
        var decoder = new BarracudaXDecoder();
        var pending = new Queue<byte[]>();
        byte sequence = 0;
        string stage = replacement ? "remote route (E1)" : "handshake";
        Task SendAsync(byte[] data, CancellationToken token) => send(data, token);
        byte[] Query(byte seq, byte command, bool remote = false) => replacement
            ? BarracudaXProtocol.ReplacementQuery(seq, command, remote)
            : BarracudaXProtocol.Query(seq, command, remote);
        async Task<byte[]?> WaitAsync(Func<byte[], bool> match, CancellationToken token, bool handshake = false, int windowMs = 1200)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(windowMs);
            try
            {
                while (true)
                {
                    while (pending.TryDequeue(out var frame))
                        if (match(frame)) return frame;
                    byte[] report = await receive(timeout.Token).ConfigureAwait(false);
                    if (handshake)
                    {
                        if (report.Length >= 4 && report[0] == 1 && report[1] == 0x40 && report[2] == 1 && report[3] == 1) return report;
                        continue;
                    }
                    foreach (byte[] frame in decoder.Feed(report, replacement)) pending.Enqueue(frame);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { logger?.LogWarning("Barracuda X query timed out at {Stage}", stage); return null; }
        }
        async Task<bool> RouteAsync(bool remote, CancellationToken token)
        {
            byte seq = ++sequence;
            await SendAsync(Query(seq, 0xE1, remote), token).ConfigureAwait(false);
            return await WaitAsync(f => BarracudaXProtocol.IsAck(f, seq), token).ConfigureAwait(false) is not null;
        }
        async Task<int?> TelemetryAsync(byte command, CancellationToken token)
        {
            await SendAsync(Query(++sequence, command), token).ConfigureAwait(false);
            byte[]? reply = await WaitAsync(f => BarracudaXProtocol.Value(f, command).HasValue, token, windowMs: replacement ? 1500 : 1200).ConfigureAwait(false);
            return reply is null ? null : BarracudaXProtocol.Value(reply, command);
        }
        bool restore = false;
        try
        {
            if (replacement)
            {
                // 1532:0550 as tested by nightdevx/barracuda-x-battery: no handshake and no E3/E0, which are only
                // documented on 0552. Remote mode is selected first and always released afterwards.
                restore = true;
                if (!await RouteAsync(true, ct).ConfigureAwait(false)) return new();
                stage = "battery (21)";
                int? percent = await TelemetryAsync(0x21, ct).ConfigureAwait(false);
                // The headset answers only while it is on and linked; no battery answer leaves the link unknown.
                if (percent is null) return new();
                stage = "charging cable (2A)";
                int? plugged = await TelemetryAsync(0x2A, ct).ConfigureAwait(false);
                return new(percent, plugged is null ? null : plugged == 1, true);
            }
            await SendAsync(BarracudaXProtocol.Handshake(), ct).ConfigureAwait(false);
            if (await WaitAsync(_ => false, ct, handshake: true).ConfigureAwait(false) is null) return new();
            stage = "wireless link (E3)";
            byte seq = ++sequence;
            await SendAsync(Query(seq, 0xE3), ct).ConfigureAwait(false);
            // A data frame's sequence is a device counter. Only the ACK echoes our sequence.
            bool acknowledged = false;
            byte[]? linkReply = null;
            byte[]? link = await WaitAsync(f =>
            {
                if (BarracudaXProtocol.IsAck(f, seq)) acknowledged = true;
                if (BarracudaXProtocol.Link(f).HasValue) linkReply = f;
                return acknowledged && linkReply is not null;
            }, ct).ConfigureAwait(false);
            if (link is null) return new();
            if (BarracudaXProtocol.Link(linkReply!) != true) return new(IsConnected: false);
            // Do not take a remote route already owned by another diagnostic application.
            stage = "local route (E0)";
            await SendAsync(Query(++sequence, 0xE0), ct).ConfigureAwait(false);
            byte[]? route = await WaitAsync(f => BarracudaXProtocol.Route(f).HasValue, ct).ConfigureAwait(false);
            if (route is null || BarracudaXProtocol.Route(route) != 0) return new(IsConnected: true);
            restore = true; // Even a cancelled or timed-out route selection must restore local mode.
            stage = "select headset route (E1)";
            if (!await RouteAsync(true, ct).ConfigureAwait(false)) return new(IsConnected: true);
            stage = "battery (21)";
            int? battery = await TelemetryAsync(0x21, ct).ConfigureAwait(false);
            stage = "charging cable (2A)";
            int? cable = await TelemetryAsync(0x2A, ct).ConfigureAwait(false);
            return new(battery, cable is null ? null : cable == 1, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger?.LogWarning("Barracuda X query failed at {Stage}: 0x{HResult:X8}", stage, ex.HResult);
            throw;
        }
        finally
        {
            if (restore)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    stage = "restore local route";
                    bool released = await RouteAsync(false, cleanup.Token).ConfigureAwait(false);
                    if (!replacement)
                    {
                        await SendAsync(Query(++sequence, 0xE0), cleanup.Token).ConfigureAwait(false);
                        released = await WaitAsync(f => BarracudaXProtocol.Route(f) == 0, cleanup.Token).ConfigureAwait(false) is not null;
                    }
                    if (!released) logger?.LogWarning("Barracuda X local route restoration was not confirmed");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { logger?.LogWarning("Barracuda X local route restoration failed: 0x{HResult:X8}", ex.HResult); }
            }
        }
    }
}
