// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Gesture, wear-detection switch and multipoint commands ported from TWS-Pods-PC
// (https://github.com/Zhaoyi-ya/TWS-Pods-PC, 3350f42) and vivopods-mac
// (https://github.com/FxRayHughes/vivopods-mac, be25133), both GPL-3.0-only.
namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>
/// vivo / iQOO compact GAIA command ids. A report or acknowledgement is the request id | 0x8000;
/// a query is the set id + 0x0100 (e.g. set 0x0130, query 0x0230, report 0x8230).
/// </summary>
internal static class VivoCommands
{
    /// <summary>Version negotiation; always GAIA vendor 0x000A and version 4.</summary>
    public const ushort Handshake = 0x0300;
    public const ushort HandshakeResponse = 0x8300;

    /// <summary>Empty frames 0x0202–0x0206 sent after the handshake make the earbuds start pushing reports.</summary>
    public static readonly ushort[] RegisterNotifications = [0x0202, 0x0203, 0x0204, 0x0205, 0x0206];

    /// <summary>Battery: report payload <c>[00][left%][right%][case%][charging bits L=1 R=2 case=4]</c>. Always GAIA version 4.</summary>
    public const ushort QueryBattery = 0x0207;
    public const ushort ReportBattery = 0x8207;

    /// <summary>Current noise mode: set payload <c>[mode, ..model suffix]</c>; report <c>[00][mode][reduce level]</c>.</summary>
    public const ushort SetNoiseMode = 0x0130;
    public const ushort AckNoiseMode = 0x8130;
    public const ushort QueryNoiseMode = 0x0230;
    public const ushort ReportNoiseMode = 0x8230;

    /// <summary>Find earbuds: set <c>[01]</c> ring / <c>[00]</c> stop; ack <c>[00][state]</c>.</summary>
    public const ushort SetFindDevice = 0x0120;
    public const ushort AckFindDevice = 0x8120;

    /// <summary>Live wear state: report <c>[00][flags]</c>, bit0/1 = left/right in case, bit2/3 = left/right worn.</summary>
    public const ushort QueryWearState = 0x020D;
    public const ushort ReportWearState = 0x820D;

    /// <summary>Built-in sound effect (device EQ preset): set <c>[id]</c>; ack/report <c>[00][id]</c>.</summary>
    public const ushort SetAudioEffect = 0x0118;
    public const ushort AckAudioEffect = 0x8118;
    public const ushort QueryAudioEffect = 0x0218;
    public const ushort ReportAudioEffect = 0x8218;

    /// <summary>Low-latency game mode: set <c>[0|1]</c>; ack/report <c>[00][state]</c>.</summary>
    public const ushort SetLowLatency = 0x0151;
    public const ushort AckLowLatency = 0x8151;
    public const ushort QueryLowLatency = 0x0251;
    public const ushort ReportLowLatency = 0x8251;

    /// <summary>Firmware version query.</summary>
    public const ushort QueryFirmware = 0x021C;
    public const ushort ReportFirmware = 0x821C;

    /// <summary>The earbuds ask for the phone's local time; answered with <see cref="HostTimeResponse"/>.</summary>
    public const ushort PeerTimeRequest = 0x8509;
    public const ushort HostTimeResponse = 0x0509;

    // Commands below are from TWS-Pods-PC vivo/PROTOCOL.md (TWS 3e fw 2.5.9 btsnoop; multipoint on
    // TWS 5 fw 2.11.1 via vivopods-mac). 0x0202 / 0x0203 double as the first two registration frames.

    /// <summary>Double-tap action, one earbud per write: set <c>[code]</c> (left 0x00–0x0F, right 0x10–0x1F); report <c>[00][left][right]</c>.</summary>
    public const ushort SetDoubleTap = 0x0102;
    public const ushort QueryDoubleTap = 0x0202;
    public const ushort ReportDoubleTap = 0x8202;

    /// <summary>Wear-detection switch (not the live wear state): set <c>[0|1]</c>; ack/report <c>[00][state]</c>.</summary>
    public const ushort SetWearDetection = 0x0103;
    public const ushort AckWearDetection = 0x8103;
    public const ushort QueryWearDetection = 0x0203;
    public const ushort ReportWearDetection = 0x8203;

    /// <summary>Long-press noise cycle per earbud: set <c>[05][left][right]</c>; query <c>[05]</c>; ack/report <c>[00][05][left][right]</c>.</summary>
    public const ushort SetNoiseCycle = 0x0131;
    public const ushort AckNoiseCycle = 0x8131;
    public const ushort QueryNoiseCycle = 0x0231;
    public const ushort ReportNoiseCycle = 0x8231;

    /// <summary>Long-press (touch operation) function for both earbuds: set <c>[03][function]</c>; ack/report <c>[00][03][function]</c>.</summary>
    public const ushort SetLongPress = 0x0150;
    public const ushort AckLongPress = 0x8150;
    public const ushort QueryLongPress = 0x0250;
    public const ushort ReportLongPress = 0x8250;

    /// <summary>Dual connection list: report <c>[00][count]</c>, then per device <c>[address:6][class:3][state][nameLength][UTF-8 name]</c>.</summary>
    public const ushort QuerySourceDevices = 0x0249;
    public const ushort ReportSourceDevices = 0x8249;

    /// <summary>Writes the whole list, <c>[address:6][state]</c> per device; untouched devices repeat their reported state.</summary>
    public const ushort SetSourceDevices = 0x014A;

    /// <summary>Forgets a pairing: <c>[target address:6][this host's address:6]</c>.</summary>
    public const ushort RemoveSourceDevice = 0x014D;
}

/// <summary>Framing constants and the control service.</summary>
internal static class VivoConstants
{
    /// <summary>vivo private RFCOMM (GAIA) service.</summary>
    public static readonly Guid ServiceUuid = new("00000837-D102-11E1-9B23-00025B00A5A5");

    /// <summary>GAIA GATT service (vivopods-mac: TWS 5); messages omit the RFCOMM <c>FF</c> header.</summary>
    public static readonly Guid GattServiceUuid = new("00001100-D102-11E1-9B23-00025B00A5A5");

    public const byte Preamble = 0xFF;
    public const ushort GaiaVendor = 0x000A;
    public const ushort VivoVendor = 0x001B;
    public const byte HandshakeVersion = 4;
    public const int HeaderLength = 8;
    public const int MaxPayload = 254;

    /// <summary>Noise mode bytes of <see cref="VivoCommands.SetNoiseMode"/> (same on every model).</summary>
    public const byte NoiseModeAnc = 0x00;
    public const byte NoiseModeOff = 0x01;
    public const byte NoiseModeTransparency = 0x02;

    /// <summary>DeepX custom sound effect id; not a built-in preset.</summary>
    public const byte DeepXCustomEffect = 0x10;

    /// <summary>Sub-command bytes of <see cref="VivoCommands.SetLongPress"/> and <see cref="VivoCommands.SetNoiseCycle"/>.</summary>
    public const byte LongPressSubCommand = 0x03;
    public const byte NoiseCycleSubCommand = 0x05;

    /// <summary>Base of the right earbud's double-tap codes.</summary>
    public const byte RightDoubleTapBase = 0x10;

    /// <summary>Source-device states in <see cref="VivoCommands.ReportSourceDevices"/> and <see cref="VivoCommands.SetSourceDevices"/>.</summary>
    public const byte SourceDisconnected = 0x00;
    public const byte SourceConnected = 0x01;
    public const byte SourceActive = 0x02;
}
