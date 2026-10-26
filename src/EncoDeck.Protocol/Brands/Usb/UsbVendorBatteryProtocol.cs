// SPDX-License-Identifier: GPL-3.0-only
// Receiver queries ported from HeyOkay/HaloBattery (MIT, Copyright (c) 2026 Halo Battery contributors),
// SnowDreamXUE/NPS.HecateBattery (MIT, Copyright (c) 2026 SnowDream), Sapd/HeadsetControl (GPL-3.0)
// and the OpenRazer BlackShark V2 Pro 2.4 driver proposal (GPL-2.0-or-later). See THIRD_PARTY_NOTICES.md.
using System.Text.RegularExpressions;

namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>What one reply proves. A null field was not reported; nothing is estimated.</summary>
public readonly record struct UsbHeadsetReading(int? Level = null, bool? IsCharging = null, bool? IsConnected = null)
{
    public bool IsEmpty => Level is null && IsCharging is null && IsConnected is null;
    public UsbHeadsetReading Merge(UsbHeadsetReading next) => new(next.Level ?? Level, next.IsCharging ?? IsCharging, next.IsConnected ?? IsConnected);
}

public enum UsbQueryKind
{
    /// <summary>Feature report written and read back (BlackShark V2 Pro 2020).</summary>
    Feature,
    /// <summary>Output report requests, each answered by its own input report.</summary>
    Request,
    /// <summary>Airoha "PA" GETs inside remote mode (E1); remote mode is always released.</summary>
    PaRemote,
    /// <summary>No request exists; only reports the receiver pushes by itself are read.</summary>
    Passive,
    /// <summary>Logitech Centurion feature discovery through the receiver bridge.</summary>
    Centurion,
}

/// <summary>One read-only request and the parser that accepts only its own answer.</summary>
public sealed record UsbRequest(byte[] Report, Func<byte[], UsbHeadsetReading?> Parse, int Attempts = 1, int WindowMs = 1000);

/// <summary>Read-only queries for individually documented receiver protocols. Never select by brand alone.</summary>
public static class UsbVendorBatteryProtocol
{
    // Page/usage null: the community sources name the collection by its declared report, not by usage,
    // so the transport additionally requires that output report to exist with the exact length.
    private sealed record Spec(string Model, UsbQueryKind Kind, ushort? Page, ushort[]? Usages, byte ReportId, int? Length = 64);

    private static Spec? Find(ushort vendor, ushort product) => (vendor, product) switch
    {
        (0x3837, 0x6045) => new("mchose-usb-x9", UsbQueryKind.Request, 0xFF90, [1], 0x55),
        (0x291D, 0x385D) => new("mchose-usb-v9-pro", UsbQueryKind.Request, 0xFF90, [1], 0x55),
        (0x1532, 0x0528) => new("razer-usb-blackshark-v2-pro-2020", UsbQueryKind.Feature, 0xFF00, [1], 0xFF),
        (0x1532, 0x0555) => new("razer-usb-blackshark-v2-pro-2023", UsbQueryKind.PaRemote, 0xFF00, [1], 2),
        // HaloBattery lists the Barracuda Pro vendor collection as ff00:0000.
        (0x1532, 0x053A) => new("razer-usb-barracuda-pro", UsbQueryKind.PaRemote, 0xFF00, [0, 1], 1),
        (0x1532, 0x053C) => new("razer-usb-barracuda", UsbQueryKind.PaRemote, 0xFF00, [0, 1], 1),
        (0x03F0, 0x08BE) => new("hyperx-usb-cloud-alpha-2", UsbQueryKind.Request, 0xFF13, [0xFF00], 0x50),
        (0x03F0, 0x02CC or 0x06BE) => new("hyperx-usb-cloud-iii-s", UsbQueryKind.Request, null, null, 0x0C),
        // No report ID: WinRT reports carry a leading zero and the declared length is used as is.
        (0x1038, 0x22A1) => new("steelseries-usb-arctis-nova-7", UsbQueryKind.Request, 0xFFC0, [1], 0, null),
        (0x35BB, 0xA226) => new("edifier-usb-g3-max", UsbQueryKind.Request, null, null, 0xD0),
        (0x0ECB, 0x2088) => new("jbl-usb-quantum-910", UsbQueryKind.Passive, 0xFF13, [1], 0),
        (0x046D, 0x0AF7) => new("logitech-usb-g-pro-x-2", UsbQueryKind.Centurion, 0xFFA0, [1], LogitechCenturionProtocol.ReportId),
        _ => null,
    };

    public static string? Model(ushort vendor, ushort product) => Find(vendor, product)?.Model;
    public static UsbQueryKind? Kind(ushort vendor, ushort product) => Find(vendor, product)?.Kind;
    public static byte ReportId(ushort vendor, ushort product) => Require(vendor, product).ReportId;
    /// <summary>Exact output report length the sources send, or null to use the declared length.</summary>
    public static int? ReportLength(ushort vendor, ushort product) => Require(vendor, product).Length;

    public static bool IsCollection(ushort vendor, ushort product, ushort page, ushort usage) =>
        Find(vendor, product) is { } spec && (spec.Page is { } exact ? page == exact && spec.Usages!.Contains(usage) : page >= 0xFF00);

    /// <summary>
    /// Exact receiver interfaces that may be opened read-write. The HECATE G3 MAX query only goes to
    /// interface 3 collection 3 (report D0); collection 2 never answers and collection 4 carries LED frames.
    /// </summary>
    public static bool IsInterface(string interfaceId)
    {
        var match = Regex.Match(interfaceId, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?=&|#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        ushort vendor = Convert.ToUInt16(match.Groups[1].Value, 16), product = Convert.ToUInt16(match.Groups[2].Value, 16);
        if (BarracudaXProtocol.IsReceiver(vendor, product)) return true;
        if (Find(vendor, product) is null) return false;
        return vendor != 0x35BB || Regex.IsMatch(interfaceId, @"&MI_03&COL03(?:&|#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Spec Require(ushort vendor, ushort product) => Find(vendor, product) ?? throw new ArgumentException("Undocumented receiver");

    /// <summary>The first request: the battery query, or the feature request of the 2020 receiver.</summary>
    public static byte[] Query(ushort vendor, ushort product)
    {
        var spec = Require(vendor, product);
        return spec.Kind switch
        {
            UsbQueryKind.Feature => Report(0xFF, 0x0A, 0, 0xFD, 4, 0x12, 0xF1, 2, 5),
            UsbQueryKind.PaRemote => Query(vendor, product, 0x21),
            UsbQueryKind.Request => Requests(vendor, product)[0].Report,
            _ => throw new ArgumentException("This receiver has no single query"),
        };
    }

    /// <summary>Requests sent one after another; every request is a read.</summary>
    public static IReadOnlyList<UsbRequest> Requests(ushort vendor, ushort product)
    {
        var spec = Require(vendor, product);
        if (spec.Kind != UsbQueryKind.Request) throw new ArgumentException("Not a request/response receiver");
        return (vendor, product) switch
        {
            // 55 65 01 asks for the value message; field 02 of the answer is the battery percentage.
            (0x3837, _) or (0x291D, _) => [new(Report(0x55, 0x65, 1), Mchose)],
            (0x03F0, 0x08BE) => [new(Report(0x50, 0x02), HyperXAlpha2, WindowMs: 1500)],
            // Byte 3 = 01 asks for a value; 00 would set one and is never sent.
            (0x03F0, _) => [new(Report(0x0C, 2, 3, 1, 0, 0x06), f => HyperXCloud3S(f, 0x06)), new(Report(0x0C, 2, 3, 1, 0, 0x48), f => HyperXCloud3S(f, 0x48))],
            (0x1038, _) => [new([0, 0xB0], ArctisNova7)],
            // The receiver answers within about 10 ms; NPS.HecateBattery retries four times in 300 ms windows.
            (0x35BB, _) => [new(Report(0xD0, 0x35, 0xBB), HecateG3Max, Attempts: 4, WindowMs: 300)],
            _ => throw new ArgumentException("Undocumented receiver"),
        };
    }

    /// <summary>Airoha GET inside the vendor envelope of a PA receiver; only battery 21 and cable 2A.</summary>
    public static byte[] Query(ushort vendor, ushort product, byte command)
    {
        if (Require(vendor, product).Kind != UsbQueryKind.PaRemote || command is not (0x21 or 0x2A))
            throw new ArgumentOutOfRangeException(nameof(command));
        return PaReport(product, 0x08, [command, 0, 0]);
    }

    /// <summary>BlackShark V2 Pro 2023 remote mode (kept for the existing 0555 path).</summary>
    public static byte[] Remote(bool enabled) => Remote(0x0555, enabled);

    public static byte[] Remote(ushort product, bool enabled)
    {
        if (Find(0x1532, product)?.Kind != UsbQueryKind.PaRemote) throw new ArgumentException("Undocumented receiver");
        return PaReport(product, 0x0E, [0xE1, enabled ? (byte)1 : (byte)0]);
    }

    // BlackShark 0555 uses the YS-Tech style [02 80 len 00 00 PA...] with sequence 0; the Barracuda Pro and
    // Barracuda receivers use [01 80 len PA...] with the sequence 08 seen in every published capture.
    private static byte[] PaReport(ushort product, byte group, byte[] body)
    {
        bool ys = product == 0x0555;
        byte[] inner = [0x50, 0x41, group, ys ? (byte)0 : (byte)8, (byte)body.Length, .. body];
        var report = new byte[64];
        report[0] = ys ? (byte)2 : (byte)1; report[1] = 0x80; report[2] = (byte)inner.Length;
        inner.CopyTo(report, ys ? 5 : 3);
        return report;
    }

    public static int? Parse(ushort vendor, ushort product, ReadOnlySpan<byte> frame)
    {
        // WinRT includes the report ID. Never try an offset heuristic or clamp an invalid percentage.
        var spec = Find(vendor, product);
        if (spec is null || frame.Length != 64) return null;
        if (spec.Kind == UsbQueryKind.Feature)
        {
            ReadOnlySpan<byte> fresh = [0xFF, 0x0F, 5, 0xFE, 0x12, 4, 0x1F, 8, 5];
            // Off/out-of-range replies retain old payload under a different header.
            // The reported level is unreliable during active charging (state 09).
            return frame.StartsWith(fresh) && (frame[12] != 0 || frame[13] != 0) && frame[11] != 9 && frame[14] <= 100 ? frame[14] : null;
        }
        if (spec.Kind == UsbQueryKind.PaRemote) return ReadPa(product, frame, 0x21)?.Level;
        return spec.Kind == UsbQueryKind.Request ? Requests(vendor, product)[0].Parse(frame.ToArray())?.Level : null;
    }

    /// <summary>
    /// Reply to a PA GET: the 'P','I' frame echoing the command as a reply (01) with one value byte.
    /// Notifications (02) carry the same layout and are not taken as answers.
    /// </summary>
    public static UsbHeadsetReading? ReadPa(ushort product, ReadOnlySpan<byte> frame, byte command)
    {
        if (Find(0x1532, product)?.Kind != UsbQueryKind.PaRemote || frame.Length != 64) return null;
        bool ys = product == 0x0555;
        int pi = ys ? 2 : 3, at = pi + 10;
        if (frame[0] != (ys ? 2 : 1) || (!ys && frame[1] != 0x80) || frame[pi] != 0x50 || frame[pi + 1] != 0x49 ||
            frame[at] != command || frame[at + 1] != 1 || frame[at + 2] != 1) return null;
        byte value = frame[at + 3];
        return command switch
        {
            0x21 when value <= 100 => new UsbHeadsetReading(Level: value),
            // 0 = on battery; the receivers report 01 or 02 with the charging cable attached.
            0x2A when value <= 2 => new UsbHeadsetReading(IsCharging: value != 0),
            _ => null,
        };
    }

    private static UsbHeadsetReading? Mchose(byte[] f) =>
        f.Length == 64 && f[0] == 0x55 && f[1] == 0x65 && f[3] == 2 && f[2] <= 100 ? new(Level: f[2]) : null;

    // 51 02 <level>; bit 7 of byte 6 followed a recharge in the published captures but is not a confirmed
    // charging flag, so it is not reported.
    private static UsbHeadsetReading? HyperXAlpha2(byte[] f) =>
        f.Length >= 7 && f[0] == 0x51 && f[1] == 0x02 && f[2] <= 100 ? new(Level: f[2]) : null;

    private static UsbHeadsetReading? HyperXCloud3S(byte[] f, byte command)
    {
        if (f.Length < 7) return null;
        // 0C .. <command> <value> answers a request; 0D <..> <kind> <value> is a notification
        // (kind 1 battery, 10 charging). 0xFF means no value and fails both checks.
        int? value = f[0] == 0x0C && f[5] == command ? f[6] :
            f[0] == 0x0D && f[4] == (command == 0x06 ? 1 : 10) ? f[5] : null;
        return (command, value) switch
        {
            (0x06, <= 100) => new UsbHeadsetReading(Level: value),
            // 0 not charging, 1 charging, 2 fully charged on the cable; other values carry no claim.
            (0x48, 0 or 1 or 2) => new UsbHeadsetReading(IsCharging: value != 0),
            _ => null,
        };
    }

    // B0 <link> <level> <status>: link 03 = headset connected, 02 = not; the dongle repeats the old level
    // while the headset is off, so only the link and status bytes decide. Status 00 off, 01/02 charging, 03 on battery.
    private static UsbHeadsetReading? ArctisNova7(byte[] f)
    {
        if (f.Length < 5 || f[0] != 0 || f[1] != 0xB0) return null;
        if (f[2] == 2 || (f[2] == 3 && f[4] == 0)) return new(IsConnected: false);
        return f[2] == 3 && f[4] is 1 or 2 or 3 && f[3] <= 100 ? new(f[3], f[4] != 3, true) : null;
    }

    // D0 35 BB 0A 00 <link> <level>: the receiver keeps answering with link 00 and level 00 while the
    // headset is off, which is not 0 %. The charging state has not been identified.
    private static UsbHeadsetReading? HecateG3Max(byte[] f)
    {
        if (f.Length != 64 || f[0] != 0xD0 || f[1] != 0x35 || f[2] != 0xBB || f[3] != 0x0A || f[5] > 1 || f[6] > 100) return null;
        return f[5] == 1 ? new(Level: f[6], IsConnected: true) : new(IsConnected: false);
    }

    /// <summary>
    /// JBL Quantum 910 pushes report 08 (level in byte 1) and report 09 (headset power, byte 1 00 off / 01 on).
    /// The level is an event, mostly sent around plugging in the charger, so it is shown only when heard.
    /// </summary>
    public static UsbHeadsetReading? ReadPushed(ushort vendor, ushort product, ReadOnlySpan<byte> f)
    {
        if (Kind(vendor, product) != UsbQueryKind.Passive || f.Length < 2) return null;
        return f[0] switch
        {
            0x08 when f[1] <= 100 => new UsbHeadsetReading(Level: f[1], IsConnected: true),
            0x09 when f[1] <= 1 => new UsbHeadsetReading(IsConnected: f[1] == 1),
            _ => null,
        };
    }

    private static byte[] Report(params byte[] prefix)
    {
        var report = new byte[64];
        prefix.CopyTo(report, 0);
        return report;
    }
}
