// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Usb;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Probe;

internal static class UsbDevicesCommand
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        using var loggers = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger = loggers.CreateLogger("USB");
        using var watcher = new UsbHeadsetWatcher();
        watcher.Start();
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < until) await Task.Delay(100, ct).ConfigureAwait(false);
        var devices = watcher.Devices;
        if (devices.Count == 0)
        {
            Console.WriteLine("No verified supported USB audio receivers found. Proprietary headset controls are not inferred.");
            return 3;
        }
        foreach (var device in devices)
        {
            var status = watcher.Container(device.Identity.DeviceId) is { } container ? await UsbHidBattery.ReadStatusAsync(container, ct, logger).ConfigureAwait(false) : new UsbHeadsetStatus();
            Console.WriteLine($"{device.Identity.Name}: receiver present; documented/standard HID battery = {status.Level?.ToString() ?? "unavailable"}; cable connected = {status.IsCharging?.ToString() ?? "unknown"}; headset radio link = {status.IsConnected?.ToString() ?? "unconfirmed"}");
        }
        return 0;
    }
}
