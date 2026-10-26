// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyOkay/HaloBattery providers/logitech_centurion.py (MIT, Copyright (c) 2026 Halo Battery
// contributors), which follows Solaar and Sapd/HeadsetControl. See THIRD_PARTY_NOTICES.md.
namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>
/// Logitech "Centurion" framing of the G PRO X 2 LIGHTSPEED receiver (046D:0AF7, collection ffa0:0001):
/// <c>51 &lt;len&gt; &lt;flags&gt; &lt;payload&gt;</c> with len = payload length + 1. Only read functions are built:
/// Root get-feature (0), FeatureSet count (0) and feature id (1), and battery state (0) of feature 0104.
/// </summary>
public static class LogitechCenturionProtocol
{
    public const byte ReportId = 0x51;
    public const ushort FeatureSet = 0x0001, Bridge = 0x0003, Battery = 0x0104;
    private const byte SoftwareId = 1, BridgeSend = 0x10;

    public enum Function : byte { GetFeature = 0x00, Count = 0x00, FeatureId = 0x10, BatteryState = 0x00 }

    /// <summary>Request to a receiver feature: <c>&lt;index&gt; &lt;function|sw&gt; &lt;params&gt;</c>.</summary>
    public static byte[] Direct(byte index, Function function, ReadOnlySpan<byte> parameters = default) =>
        Frame([index, (byte)((byte)function | SoftwareId), .. parameters]);

    /// <summary>Request forwarded to the headset: <c>&lt;bridge&gt; 11 &lt;size:2&gt; 00 &lt;index&gt; &lt;function|sw&gt; &lt;params&gt;</c>.</summary>
    public static byte[] Bridged(byte bridge, byte index, Function function, ReadOnlySpan<byte> parameters = default)
    {
        byte[] sub = [0, index, (byte)((byte)function | SoftwareId), .. parameters];
        return Frame([bridge, BridgeSend | SoftwareId, (byte)((sub.Length >> 8) & 0x0F), (byte)sub.Length, .. sub]);
    }

    private static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 61) throw new ArgumentOutOfRangeException(nameof(payload));
        var report = new byte[64];
        report[0] = ReportId; report[1] = (byte)(payload.Length + 1);
        payload.CopyTo(report.AsSpan(3));
        return report;
    }

    /// <summary>The payload of a received frame, or null for any other report.</summary>
    public static byte[]? Payload(ReadOnlySpan<byte> report)
    {
        if (report.Length < 4 || report[0] != ReportId || report[1] <= 1 || report[1] + 2 > report.Length) return null;
        return report[3..(2 + report[1])].ToArray();
    }

    public enum ReplyKind { None, Data, Error, Accepted, Rejected }

    /// <summary>Answer of the receiver itself to <see cref="Direct"/>: the data after the echoed index and function.</summary>
    public static (ReplyKind Kind, byte[] Data) DirectReply(ReadOnlySpan<byte> report, byte index, Function function)
    {
        byte fn = (byte)((byte)function | SoftwareId);
        if (Payload(report) is not { Length: >= 2 } p) return (ReplyKind.None, []);
        if (p[0] == 0xFF && p.Length >= 3 && p[1] == index && p[2] == fn) return (ReplyKind.Error, []);
        return p[0] == index && p[1] == fn ? (ReplyKind.Data, p[2..]) : (ReplyKind.None, []);
    }

    /// <summary>
    /// The receiver first acknowledges a bridged request (<c>&lt;bridge&gt; 11</c>); the headset's answer is
    /// <c>&lt;bridge&gt; 10 &lt;size:2&gt; 00 &lt;index&gt; &lt;function|sw&gt; &lt;data&gt;</c>, index FF when it rejects it.
    /// Headset notifications (other function bytes) are not answers.
    /// </summary>
    public static (ReplyKind Kind, byte[] Data) BridgeReply(ReadOnlySpan<byte> report, byte bridge, byte index, Function function)
    {
        byte fn = (byte)((byte)function | SoftwareId);
        if (Payload(report) is not { Length: >= 2 } p || p[0] != bridge || (p[1] >> 4) != (BridgeSend >> 4)) return (ReplyKind.None, []);
        if ((p[1] & 0x0F) == SoftwareId) return (ReplyKind.Accepted, []);
        if ((p[1] & 0x0F) != 0 || p.Length < 7 || p[4] != 0) return (ReplyKind.None, []);
        if (p[5] == 0xFF && p[6] == index) return (ReplyKind.Rejected, []);
        return p[5] == index && p[6] == fn ? (ReplyKind.Data, p[7..]) : (ReplyKind.None, []);
    }

    /// <summary>Feature id reported by FeatureSet function 1: <c>&lt;n&gt; &lt;id:2&gt; ...</c>.</summary>
    public static ushort? FeatureId(ReadOnlySpan<byte> data) => data.Length >= 3 ? (ushort)(data[1] << 8 | data[2]) : null;

    /// <summary>Battery feature 0104 function 0: percent, then the charging state at byte 2
    /// (0 discharging, 1 charging, 2 charging over USB, 3 complete).</summary>
    public static UsbHeadsetReading? ReadBattery(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data[0] > 100) return null;
        bool? charging = data.Length >= 3 && data[2] <= 3 ? data[2] != 0 : null;
        return new(data[0], charging, true);
    }
}
