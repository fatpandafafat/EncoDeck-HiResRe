// SPDX-License-Identifier: GPL-3.0-only
// Request/retry timing follows HeyOkay/HaloBattery (MIT, Copyright (c) 2026 Halo Battery contributors) and
// SnowDreamXUE/NPS.HecateBattery (MIT, Copyright (c) 2026 SnowDream); see THIRD_PARTY_NOTICES.md.
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Usb;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Foundation;

namespace EncoDeck.Bluetooth.Usb;

internal static class UsbVendorBattery
{
    public static async Task<UsbHeadsetStatus?> ReadAsync(HidDevice device, string interfaceId, CancellationToken ct)
    {
        ushort vendor = device.VendorId, product = device.ProductId;
        if (!UsbVendorBatteryProtocol.IsCollection(vendor, product, device.UsagePage, device.UsageId) ||
            UsbVendorBatteryProtocol.Kind(vendor, product) is not { } kind) return null;
        if (kind == UsbQueryKind.Feature)
            return await ReadFeatureAsync(device, ct).ConfigureAwait(false) is { } level ? new(level) : null;

        // A collection picked by its declared report must declare exactly that report; creating the
        // report sends nothing, so an unrelated collection never receives a vendor command.
        int? exact = UsbVendorBatteryProtocol.ReportLength(vendor, product);
        if (kind != UsbQueryKind.Passive)
        {
            try
            {
                var declared = device.CreateOutputReport(UsbVendorBatteryProtocol.ReportId(vendor, product));
                if (exact is { } length && declared.Data.Length != length) return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { return null; }
        }

        var reports = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true, SingleWriter = false });
        TypedEventHandler<HidDevice, HidInputReportReceivedEventArgs> handler = (_, args) => reports.Writer.TryWrite(args.Report.Data.ToArray());
        Task<byte[]> ReceiveAsync(CancellationToken token) => reports.Reader.ReadAsync(token).AsTask();
        async Task SendAsync(byte[] data, CancellationToken token)
        {
            var report = device.CreateOutputReport(data[0]);
            int size = (int)report.Data.Length;
            if (data.Length > size || (exact is { } length && size != length)) throw new IOException("Unexpected HID report length");
            // Zero padding up to the declared length, as the reference tools send it.
            var buffer = new byte[size];
            data.CopyTo(buffer, 0);
            report.Data = buffer.AsBuffer();
            // SendOutputReportAsync writes through the interrupt path; some receivers acknowledge a control
            // SET_REPORT without acting on it, so that path is never used for vendor commands.
            uint written = await device.SendOutputReportAsync(report).AsTask(token).WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            if (written != size) throw new IOException("Incomplete HID output report");
        }
        device.InputReportReceived += handler;
        try
        {
            var reading = kind switch
            {
                UsbQueryKind.Request => await RequestAsync(UsbVendorBatteryProtocol.Requests(vendor, product), SendAsync, ReceiveAsync, ct).ConfigureAwait(false),
                UsbQueryKind.PaRemote => await PaRemoteAsync(product, SendAsync, ReceiveAsync, ct).ConfigureAwait(false),
                UsbQueryKind.Passive => await ListenAsync(f => UsbVendorBatteryProtocol.ReadPushed(vendor, product, f), ReceiveAsync, ct).ConfigureAwait(false),
                UsbQueryKind.Centurion => await LogitechCenturionBattery.ReadReportsAsync(interfaceId, SendAsync, ReceiveAsync, ct).ConfigureAwait(false),
                _ => new UsbHeadsetReading(),
            };
            return reading.IsEmpty ? null : new(reading.Level, reading.IsCharging, reading.IsConnected);
        }
        finally { device.InputReportReceived -= handler; }
    }

    private static async Task<int?> ReadFeatureAsync(HidDevice device, CancellationToken ct)
    {
        ushort vendor = device.VendorId, product = device.ProductId;
        var feature = device.CreateFeatureReport(0xFF);
        if (feature.Data.Length != 64) return null;
        feature.Data = UsbVendorBatteryProtocol.Query(vendor, product).AsBuffer();
        await device.SendFeatureReportAsync(feature).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var reply = await device.GetFeatureReportAsync(0xFF).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (UsbVendorBatteryProtocol.Parse(vendor, product, reply.Data.ToArray()) is { } level) return level;
        }
        return null;
    }

    /// <summary>Each request in turn; a request without an answer ends the read, and so does a headset reported off.</summary>
    internal static async Task<UsbHeadsetReading> RequestAsync(IReadOnlyList<UsbRequest> requests,
        Func<byte[], CancellationToken, Task> send, Func<CancellationToken, Task<byte[]>> receive, CancellationToken ct)
    {
        var reading = new UsbHeadsetReading();
        foreach (var request in requests)
        {
            UsbHeadsetReading? answer = null;
            for (int attempt = 0; attempt < request.Attempts && answer is null; attempt++)
            {
                if (attempt > 0) await Task.Delay(100, ct).ConfigureAwait(false);
                await send(request.Report, ct).ConfigureAwait(false);
                answer = await WaitAsync(receive, request.Parse, request.WindowMs, ct).ConfigureAwait(false);
            }
            if (answer is not { } value) break;
            reading = reading.Merge(value);
            if (value.IsConnected == false) break;
        }
        return reading;
    }

    /// <summary>
    /// PA receivers ignore a GET outside remote mode, and the first frame after about 0.3 s of idle only wakes
    /// the link, so a sacrificial remote-on precedes the queries and every attempt re-asserts remote mode.
    /// Remote mode is released even when the caller cancels or the link stops responding.
    /// </summary>
    internal static async Task<UsbHeadsetReading> PaRemoteAsync(ushort product,
        Func<byte[], CancellationToken, Task> send, Func<CancellationToken, Task<byte[]>> receive, CancellationToken ct)
    {
        var reading = new UsbHeadsetReading();
        // Retries must land before the BlackShark link dozes again; the Barracuda receivers answer more slowly.
        int window = product == 0x0555 ? 200 : 600;
        try
        {
            await send(UsbVendorBatteryProtocol.Remote(product, true), ct).ConfigureAwait(false);
            await Task.Delay(35, ct).ConfigureAwait(false);
            foreach (byte command in new byte[] { 0x21, 0x2A })
            {
                UsbHeadsetReading? answer = null;
                for (int attempt = 0; attempt < 3 && answer is null; attempt++)
                {
                    await send(UsbVendorBatteryProtocol.Remote(product, true), ct).ConfigureAwait(false);
                    await Task.Delay(35, ct).ConfigureAwait(false);
                    await send(UsbVendorBatteryProtocol.Query(0x1532, product, command), ct).ConfigureAwait(false);
                    answer = await WaitAsync(receive, f => UsbVendorBatteryProtocol.ReadPa(product, f, command), window, ct).ConfigureAwait(false);
                }
                // Without a battery answer the headset is off: the cable query would only wait out its retries.
                if (answer is not { } value) break;
                reading = reading.Merge(value);
            }
            return reading;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await send(UsbVendorBatteryProtocol.Remote(product, false), cleanup.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }

    /// <summary>Collects what a receiver pushes by itself during a short window; nothing is sent.</summary>
    internal static async Task<UsbHeadsetReading> ListenAsync(Func<byte[], UsbHeadsetReading?> parse,
        Func<CancellationToken, Task<byte[]>> receive, CancellationToken ct, int windowMs = 1500)
    {
        var reading = new UsbHeadsetReading();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(windowMs);
        try
        {
            while (true)
                if (parse(await receive(timeout.Token).ConfigureAwait(false)) is { } value) reading = reading.Merge(value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return reading; }
    }

    internal static async Task<UsbHeadsetReading?> WaitAsync(Func<CancellationToken, Task<byte[]>> receive,
        Func<byte[], UsbHeadsetReading?> parse, int windowMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(windowMs);
        try
        {
            while (true)
                if (parse(await receive(timeout.Token).ConfigureAwait(false)) is { } value) return value;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
