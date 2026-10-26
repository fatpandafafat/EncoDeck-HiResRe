// SPDX-License-Identifier: GPL-3.0-only
// Sessions ported from nightdevx/barracuda-x-battery barracuda.py (MIT, Copyright (c) 2026 nightdijital) for
// 1532:0550 and from the OpenRazer razerblackshark driver proposal (openrazer/openrazer#2862) and
// AbdullahSAhmad/blackshark-control (GPL-2.0-or-later) for 1532:0555. See THIRD_PARTY_NOTICES.md.
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using EncoDeck.Protocol.Brands.Usb;

namespace EncoDeck.Bluetooth.Usb;

/// <summary>
/// Headset settings stored in Barracuda X (0550) and BlackShark V2 Pro 2.4 (0555) headsets. Every write is
/// confirmed only by reading the same register back; remote mode is released afterwards in every case.
/// Only the Barracuda X settings are offered in the UI (<see cref="UsbHeadsetSettingsController"/>).
/// </summary>
public static class UsbHeadsetSettings
{
    public static async Task<IReadOnlyDictionary<RazerPaSetting, byte[]>> ReadAsync(Guid container, ushort product, IEnumerable<RazerPaSetting> settings, CancellationToken ct)
    {
        using var device = await OpenAsync(container, product, ct).ConfigureAwait(false);
        if (device is null) return new Dictionary<RazerPaSetting, byte[]>();
        var (receive, detach) = BarracudaXBattery.Listen(device);
        try { return await ReadReportsAsync(device.ProductId, settings, BarracudaXBattery.Sender(device, device.ProductId == RazerPaSettings.BarracudaX), receive, ct).ConfigureAwait(false); }
        finally { detach(); }
    }

    /// <summary>True only when the headset reports the written values back.</summary>
    public static async Task<bool> WriteAsync(Guid container, ushort product, RazerPaSetting setting, byte[] values, CancellationToken ct)
    {
        using var device = await OpenAsync(container, product, ct).ConfigureAwait(false);
        if (device is null || !RazerPaSettings.IsValid(device.ProductId, setting, values)) return false;
        var (receive, detach) = BarracudaXBattery.Listen(device);
        try { return await WriteReportsAsync(device.ProductId, setting, values, BarracudaXBattery.Sender(device, device.ProductId == RazerPaSettings.BarracudaX), receive, ct).ConfigureAwait(false); }
        finally { detach(); }
    }

    private static async Task<HidDevice?> OpenAsync(Guid container, ushort product, CancellationToken ct)
    {
        foreach (var info in await UsbHidBattery.CollectionsAsync(container, ct).ConfigureAwait(false))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(info.Id, $@"VID_1532&PID_{product:X4}(?:&|#)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            var device = await HidDevice.FromIdAsync(info.Id, FileAccessMode.ReadWrite).AsTask(ct).ConfigureAwait(false);
            if (device is not null && device.VendorId == 0x1532 && device.ProductId == product && product is RazerPaSettings.BarracudaX or RazerPaSettings.BlackSharkV2Pro &&
                device.UsagePage == 0xFF00 && device.UsageId == 1) return device;
            device?.Dispose();
        }
        return null;
    }

    internal static async Task<IReadOnlyDictionary<RazerPaSetting, byte[]>> ReadReportsAsync(ushort product, IEnumerable<RazerPaSetting> settings,
        Func<byte[], CancellationToken, Task> send, Func<CancellationToken, Task<byte[]>> receive, CancellationToken ct)
    {
        var session = new Session(product, send, receive);
        var values = new Dictionary<RazerPaSetting, byte[]>();
        try
        {
            if (!await session.EnterAsync(ct).ConfigureAwait(false)) return values;
            foreach (var setting in settings.Where(s => RazerPaSettings.Supports(product, s)))
                if (await session.GetAsync(setting, ct).ConfigureAwait(false) is { } value) values[setting] = value;
            return values;
        }
        finally { await session.LeaveAsync().ConfigureAwait(false); }
    }

    internal static async Task<bool> WriteReportsAsync(ushort product, RazerPaSetting setting, byte[] values,
        Func<byte[], CancellationToken, Task> send, Func<CancellationToken, Task<byte[]>> receive, CancellationToken ct)
    {
        if (!RazerPaSettings.IsValid(product, setting, values)) return false;
        var session = new Session(product, send, receive);
        try
        {
            if (!await session.EnterAsync(ct).ConfigureAwait(false)) return false;
            if (product == RazerPaSettings.BlackSharkV2Pro && setting == RazerPaSetting.EqPreset)
            {
                // Crossing between the classic and game-specific families first only switches the family, so the
                // selector is repeated until the read-back shows the requested preset (two tries on hardware).
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    await session.ApplyPresetAsync(values[0], ct).ConfigureAwait(false);
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    if (await session.GetAsync(setting, ct).ConfigureAwait(false) is { } preset && preset.AsSpan().SequenceEqual(values)) return true;
                }
                return false;
            }
            await session.SetAsync(setting, values, ct).ConfigureAwait(false);
            return await session.GetAsync(setting, ct).ConfigureAwait(false) is { } readBack && readBack.AsSpan().SequenceEqual(values);
        }
        finally { await session.LeaveAsync().ConfigureAwait(false); }
    }

    private sealed class Session(ushort product, Func<byte[], CancellationToken, Task> send, Func<CancellationToken, Task<byte[]>> receive)
    {
        private readonly BarracudaXDecoder _decoder = new();
        private readonly Queue<byte[]> _pending = new();
        private byte _sequence;
        private bool _remote;
        private bool Barracuda => product == RazerPaSettings.BarracudaX;
        private byte Next() => _sequence = (byte)(_sequence % 127 + 1);

        public async Task<bool> EnterAsync(CancellationToken ct)
        {
            if (product is not (RazerPaSettings.BarracudaX or RazerPaSettings.BlackSharkV2Pro)) return false;
            _remote = true;
            if (!Barracuda)
            {
                // Sacrificial frame: the first frame after the link dozes is dropped.
                await RemoteAsync(true, ct).ConfigureAwait(false);
                return true;
            }
            byte seq = Next();
            await send(RazerPaSettings.Remote(product, seq, true), ct).ConfigureAwait(false);
            return await WaitAsync(f => BarracudaXProtocol.IsAck(f, seq), 800, ct).ConfigureAwait(false) is not null;
        }

        public async Task LeaveAsync()
        {
            if (!_remote) return;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                byte seq = Next();
                await send(RazerPaSettings.Remote(product, seq, false), cleanup.Token).ConfigureAwait(false);
                if (Barracuda) await WaitAsync(f => BarracudaXProtocol.IsAck(f, seq), 800, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }

        public async Task<byte[]?> GetAsync(RazerPaSetting setting, CancellationToken ct)
        {
            for (int attempt = 0; attempt < (Barracuda ? 1 : 3); attempt++)
            {
                if (!Barracuda) await RemoteAsync(true, ct).ConfigureAwait(false);
                await send(RazerPaSettings.Get(product, Next(), setting), ct).ConfigureAwait(false);
                if (await WaitAsync(f => RazerPaSettings.Values(f, setting) is not null, Barracuda ? 1500 : 200, ct).ConfigureAwait(false) is { } reply)
                    return RazerPaSettings.Values(reply, setting);
            }
            return null;
        }

        public async Task SetAsync(RazerPaSetting setting, byte[] values, CancellationToken ct)
        {
            if (!Barracuda) await RemoteAsync(true, ct).ConfigureAwait(false);
            await send(RazerPaSettings.Set(product, Next(), setting, values), ct).ConfigureAwait(false);
            // The acknowledgement is not proof; the read-back that follows decides.
            await WaitAsync(f => RazerPaSettings.IsSetReply(f, setting), Barracuda ? 1500 : 200, ct).ConfigureAwait(false);
        }

        /// <summary>BlackShark selector: remote x2, GET 1E, preset, family flag, remote; no band frame.</summary>
        public async Task ApplyPresetAsync(byte preset, CancellationToken ct)
        {
            await RemoteAsync(true, ct).ConfigureAwait(false);
            await RemoteAsync(true, ct).ConfigureAwait(false);
            await send(RazerPaSettings.PrepareEq(0), ct).ConfigureAwait(false);
            await RemoteAsync(true, ct).ConfigureAwait(false);
            await send(RazerPaSettings.Set(product, 0, RazerPaSetting.EqPreset, [preset]), ct).ConfigureAwait(false);
            await RemoteAsync(true, ct).ConfigureAwait(false);
            await send(RazerPaSettings.EqFamily(0, preset), ct).ConfigureAwait(false);
            await RemoteAsync(true, ct).ConfigureAwait(false);
        }

        private async Task RemoteAsync(bool enabled, CancellationToken ct)
        {
            await send(RazerPaSettings.Remote(product, 0, enabled), ct).ConfigureAwait(false);
            await Task.Delay(35, ct).ConfigureAwait(false);
        }

        private async Task<byte[]?> WaitAsync(Func<byte[], bool> match, int windowMs, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(windowMs);
            try
            {
                while (true)
                {
                    while (_pending.TryDequeue(out var frame))
                        if (match(frame)) return frame;
                    foreach (byte[] frame in _decoder.Feed(await receive(timeout.Token).ConfigureAwait(false), replacement: true))
                        _pending.Enqueue(frame);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
    }
}
