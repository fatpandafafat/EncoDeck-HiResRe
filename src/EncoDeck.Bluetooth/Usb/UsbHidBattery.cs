// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using EncoDeck.Protocol.Brands.Usb;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.Usb;

/// <summary>Descriptor-declared percentages and individually documented receiver queries.</summary>
public static class UsbHidBattery
{
    public static bool IsPercentage(ushort page, ushort usage, long minimum, long maximum) =>
        ((page == 0x06 && usage == 0x20) || (page == 0x85 && usage == 0x64)) && minimum == 0 && maximum == 100;
    public static async Task<int?> ReadAsync(Guid container, CancellationToken ct) =>
        (await ReadStatusAsync(container, ct).ConfigureAwait(false)).Level;

    internal static async Task<IReadOnlyList<DeviceInformation>> CollectionsAsync(Guid container, CancellationToken ct)
    {
        var interfaces = await DeviceInformation.FindAllAsync("System.Devices.InterfaceClassGuid:=\"{4d1e55b2-f16f-11cf-88cb-001111000030}\"",
            [UsbHeadsetWatcher.ContainerKey], DeviceInformationKind.DeviceInterface).AsTask(ct).ConfigureAwait(false);
        return interfaces.Where(i => i.Properties.TryGetValue(UsbHeadsetWatcher.ContainerKey, out var value) && value is Guid id && id == container).ToList();
    }

    public static async Task<UsbHeadsetStatus> ReadStatusAsync(Guid container, CancellationToken ct, ILogger? logger = null)
    {
        int collections = 0;
        foreach (var info in await CollectionsAsync(container, ct).ConfigureAwait(false))
        {
            collections++;
            ct.ThrowIfCancellationRequested();
            try
            {
                // Read-write access is requested only after exact hardware ID filtering.
                // Unrelated collections in the same container still receive no vendor command.
                if (UsbVendorBatteryProtocol.IsInterface(info.Id))
                {
                    using var vendorDevice = await HidDevice.FromIdAsync(info.Id, FileAccessMode.ReadWrite).AsTask(ct).ConfigureAwait(false);
                    if (vendorDevice is null)
                        logger?.LogWarning("USB vendor HID access denied or unavailable ({Hardware})",
                            System.Text.RegularExpressions.Regex.Match(info.Id, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value);
                    else
                        logger?.LogInformation("USB HID VID={Vendor:X4} PID={Product:X4} usage={Page:X4}:{Usage:X4}",
                            vendorDevice.VendorId, vendorDevice.ProductId, vendorDevice.UsagePage, vendorDevice.UsageId);
                    if (vendorDevice is not null && BarracudaXProtocol.IsCollection(vendorDevice.VendorId, vendorDevice.ProductId, vendorDevice.UsagePage, vendorDevice.UsageId))
                        return await BarracudaXBattery.ReadAsync(vendorDevice, ct, logger).ConfigureAwait(false);
                    if (vendorDevice is not null && UsbVendorBatteryProtocol.IsCollection(vendorDevice.VendorId, vendorDevice.ProductId, vendorDevice.UsagePage, vendorDevice.UsageId))
                        if (await UsbVendorBattery.ReadAsync(vendorDevice, info.Id, ct).ConfigureAwait(false) is { } status) return status;
                }
                using var device = await HidDevice.FromIdAsync(info.Id, FileAccessMode.Read).AsTask(ct).ConfigureAwait(false);
                if (device is null) continue;
                foreach (var (page, usage) in new (ushort, ushort)[] { (0x06, 0x20), (0x85, 0x64) })
                foreach (var descriptor in device.GetNumericControlDescriptions(HidReportType.Feature, page, usage))
                {
                    if (!IsPercentage(page, usage, descriptor.LogicalMinimum, descriptor.LogicalMaximum)) continue;
                    var report = await device.GetFeatureReportAsync(descriptor.ReportId).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    long value = report.GetNumericControlByDescription(descriptor).Value;
                    if (value is >= 0 and <= 100) return new((int)value);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            { logger?.LogWarning("USB HID collection read failed: 0x{HResult:X8}", ex.HResult); }
        }
        logger?.LogInformation("USB battery unavailable after examining {Count} HID collections", collections);
        return new();
    }
}
