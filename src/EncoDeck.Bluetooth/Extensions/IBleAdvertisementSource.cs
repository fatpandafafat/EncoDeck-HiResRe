// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Privacy;

namespace EncoDeck.Bluetooth.Extensions;

/// <summary>
/// One OPPO/OnePlus BLE manufacturer-data advertisement (company id 0x079A or 0x072F).
/// Field layout documented in docs/PROTOCOL_FINDINGS.md §6.
/// </summary>
public sealed record OppoAdvertisement(
    BluetoothAddress Address,
    uint ProductId,
    ushort Flags,
    byte? EarbudsStatus,
    byte[]? Battery,
    short Rssi,
    DateTimeOffset Timestamp);

/// <summary>
/// Extension point for lid-open popups driven by BLE advertisements.
/// Intentionally not implemented: no advertisement from an Enco X4 has been captured and verified
/// yet, and the popup must not be triggered by guessed frame layouts (see docs/PROTOCOL_FINDINGS.md §6).
/// </summary>
public interface IBleAdvertisementSource : IDisposable
{
    event EventHandler<OppoAdvertisement>? Received;

    void Start();

    void Stop();
}

// GATT (LE Audio) transport: implement EncoDeck.Protocol.Transport.IChannelConnector with the
// service 0000079A-…, write characteristic 0100079A-… and notify characteristic 0200079A-…;
// the frame format is identical, so OppoSession and OppoDeviceController are reused unchanged.
