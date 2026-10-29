// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Discovery;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Protocol.Brands.Sony;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;

namespace EncoDeck.Probe;

/// <summary>
/// Diagnostic: dumps what Windows' SDP reports for the two Sony MDR control services, including the
/// RFCOMM channel the connector parses out of each record. Read-only.
/// </summary>
internal static class SonyServicesCommand
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        var catalog = new BluetoothDeviceCatalog();
        var picked = await DeviceSelector.SelectAsync(catalog, options, ct);
        if (picked is null) return 3;

        using var device = await BluetoothDevice.FromIdAsync(picked.DeviceId).AsTask(ct);
        if (device is null) { Console.Error.WriteLine("Device not found."); return 3; }

        Console.WriteLine($"Device: {device.Name} ({(options.Has("show-address") ? $"0x{device.BluetoothAddress:X12}" : "redacted")})");
        Console.WriteLine($"Classic link: {device.ConnectionStatus}");
        Console.WriteLine($"KnownSonyControlChannel: {RfcommChannelConnector.KnownSonyControlChannel(device.Name)?.ToString() ?? "null"}");

        foreach (var uuid in new[] { SonyAkgProfiles.PrimaryServiceUuid, SonyAkgProfiles.LegacyServiceUuid })
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"\n=== {uuid:D} ===");
            var services = await device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(uuid), BluetoothCacheMode.Uncached).AsTask(ct);
            Console.WriteLine($"  error: {services.Error}; services: {services.Services.Count}");
            foreach (var service in services.Services)
            {
                using (service)
                {
                    Console.WriteLine($"  serviceId: {service.ServiceId.AsString()}");
                    Console.WriteLine($"  connectionServiceName: {service.ConnectionServiceName}");
                    Console.WriteLine($"  resolved channel: {RfcommChannelConnector.ResolveRfcommChannel(service.ConnectionServiceName)} (0 = connector cannot use this record)");
                }
            }
        }
        return 0;
    }
}
