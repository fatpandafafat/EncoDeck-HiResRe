// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyOkay/HaloBattery providers/logitech_centurion.py (MIT, Copyright (c) 2026 Halo Battery
// contributors); see THIRD_PARTY_NOTICES.md.
using System.Collections.Concurrent;
using EncoDeck.Protocol.Brands.Usb;
using static EncoDeck.Protocol.Brands.Usb.LogitechCenturionProtocol;

namespace EncoDeck.Bluetooth.Usb;

/// <summary>
/// G PRO X 2 LIGHTSPEED: list the receiver's features to find the bridge, list the headset's features through
/// it to find battery feature 0104, then read the battery. Discovery runs once per receiver and again after
/// any error; every request is a read.
/// </summary>
internal static class LogitechCenturionBattery
{
    private static readonly ConcurrentDictionary<string, (byte Bridge, byte Battery)> Known = new(StringComparer.OrdinalIgnoreCase);
    private const int WindowMs = 1500;

    private sealed class HeadsetOffline : Exception;

    internal static async Task<UsbHeadsetReading> ReadReportsAsync(string key,
        Func<byte[], CancellationToken, Task> send, Func<CancellationToken, Task<byte[]>> receive, CancellationToken ct)
    {
        async Task<byte[]?> DirectAsync(byte index, Function function, params byte[] parameters)
        {
            await send(Direct(index, function, parameters), ct).ConfigureAwait(false);
            var reply = await WaitAsync(r =>
            {
                var (kind, data) = DirectReply(r, index, function);
                return kind == ReplyKind.None ? null : (kind, data);
            }).ConfigureAwait(false);
            return reply is (ReplyKind.Data, var bytes) ? bytes : null;
        }
        async Task<byte[]?> BridgedAsync(byte bridge, byte index, Function function, params byte[] parameters)
        {
            await send(Bridged(bridge, index, function, parameters), ct).ConfigureAwait(false);
            bool accepted = false;
            var reply = await WaitAsync(r =>
            {
                var (kind, data) = BridgeReply(r, bridge, index, function);
                if (kind == ReplyKind.Accepted) { accepted = true; return null; }
                return kind == ReplyKind.None ? null : (kind, data);
            }).ConfigureAwait(false);
            // The receiver took the message but the headset never answered: it is off or out of range.
            if (reply is null && accepted) throw new HeadsetOffline();
            return reply is (ReplyKind.Data, var bytes) ? bytes : null;
        }
        async Task<(ReplyKind, byte[])?> WaitAsync(Func<byte[], (ReplyKind, byte[])?> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WindowMs);
            try
            {
                while (true)
                    if (match(await receive(timeout.Token).ConfigureAwait(false)) is { } reply) return reply;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        }
        async Task<byte?> FindAsync(Func<byte, Function, byte[], Task<byte[]?>> ask, ushort feature)
        {
            if (await ask(0, Function.GetFeature, [FeatureSet >> 8, FeatureSet & 0xFF]).ConfigureAwait(false) is not [> 0 and var set, ..]) return null;
            if (await ask(set, Function.Count, []).ConfigureAwait(false) is not [var count, ..]) return null;
            for (byte n = 0; n < count; n++)
                if (await ask(set, Function.FeatureId, [n]).ConfigureAwait(false) is { } data && LogitechCenturionProtocol.FeatureId(data) == feature) return n;
            return null;
        }

        try
        {
            if (!Known.TryGetValue(key, out var found))
            {
                if (await FindAsync((i, f, p) => DirectAsync(i, f, p), Bridge).ConfigureAwait(false) is not { } bridge) return new();
                if (await FindAsync((i, f, p) => BridgedAsync(bridge, i, f, p), Battery).ConfigureAwait(false) is not { } battery) return new();
                Known[key] = found = (bridge, battery);
            }
            if (await BridgedAsync(found.Bridge, found.Battery, Function.BatteryState).ConfigureAwait(false) is { } state &&
                ReadBattery(state) is { } reading) return reading;
            Known.TryRemove(key, out _);
            return new();
        }
        catch (HeadsetOffline) { return new(IsConnected: false); }
    }
}
