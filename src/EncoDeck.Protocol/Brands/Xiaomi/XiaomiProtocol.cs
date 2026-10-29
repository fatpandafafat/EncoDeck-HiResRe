// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Opcodes, device-info / run-info records and the battery byte layout follow BudsLink
// (https://github.com/maniacx/BudsLink, GPL-3.0), redmi-buds-bar by robin-liquidium
// (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0) and mebuds
// (https://github.com/paradox-ng/mebuds, GPL-3.0-or-later). Service UUIDs and the info-mask style
// are also documented by android_packages_apps_XiaomiTWS (Apache-2.0, see THIRD_PARTY_NOTICES.md).
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>Wire constants and record parsers of the Xiaomi RCSP control channel.</summary>
public static class XiaomiProtocol
{
    /// <summary>Xiaomi fast-connect SPP service <c>0xFD2D</c>; the service the earbuds advertise first.</summary>
    public static readonly Guid FastConnectSppUuid = new("0000FD2D-0000-1000-8000-00805F9B34FB");

    /// <summary>XIAOAI SPP service (non-standard base <c>…008584D01810</c>), the older fallback.</summary>
    public static readonly Guid XiaoAiSppUuid = new("00001101-0000-1000-8000-008584D01810");

    /// <summary>
    /// The same frames over BLE GATT (service <c>0xAF00</c>, written on <c>0xAF07</c>, answered on
    /// <c>0xAF08</c>), as redmi-buds-bar documents for the REDMI Buds 8 Pro. Its LE Audio endpoint needs
    /// no auth. Used when SPP cannot open, e.g. one earbud worn on LE Audio without a Classic link.
    /// </summary>
    public static readonly Guid MmaGattServiceUuid = new("0000AF00-0000-1000-8000-00805F9B34FB");

    public static readonly Guid MmaGattWriteUuid = new("0000AF07-0000-1000-8000-00805F9B34FB");

    public static readonly Guid MmaGattNotifyUuid = new("0000AF08-0000-1000-8000-00805F9B34FB");

    /// <summary>Type byte of phone requests that expect a reply.</summary>
    public const byte KindRequest = 0xC4;

    /// <summary>Type byte of phone responses to earbud requests.</summary>
    public const byte KindResponse = 0x04;

    /// <summary>Device information (name, firmware, VID/PID, battery…), mask <c>FF FF FF FF</c>.</summary>
    public const byte OpTargetInfo = 0x02;

    /// <summary>Set one device-info record <c>[len][type][value]</c> (in-ear detection switch).</summary>
    public const byte OpSetDeviceInfo = 0x08;

    /// <summary>Device run information <c>[len][type][value]</c> records.</summary>
    public const byte OpRunInfo = 0x09;

    /// <summary>Earbud status push (battery and noise mode records).</summary>
    public const byte OpReportStatus = 0x0E;

    /// <summary>Auth challenge: <c>01 || random(16)</c>, answered with <c>01 || E(random)</c>. Both sides send one.</summary>
    public const byte OpAuthCheck = 0x50;

    /// <summary>Auth verdict: <c>01 00</c> when the peer's answer was right; acknowledged with <c>01</c>.</summary>
    public const byte OpAuthResult = 0x51;

    public const byte OpSetConfig = 0xF2;

    public const byte OpGetConfig = 0xF3;

    public const byte OpNotifyConfig = 0xF4;

    /// <summary>Response status for an unsupported setting or command.</summary>
    public const byte StatusUnsupported = 0x04;

    /// <summary>First sequence byte of phone requests.</summary>
    public const byte ConfigSequenceStart = 0x90;

    /// <summary>Requests every device-info record.</summary>
    public static ReadOnlySpan<byte> TargetInfoQueryPayload => [0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Run-info mask for record <see cref="RunInfoWearDetection"/> only (bit 10).</summary>
    public static ReadOnlySpan<byte> RunInfoWearQueryPayload => [0x00, 0x00, 0x04, 0x00];

    /// <summary>Run-info record: in-ear detection switch, inverted (0 = on, 1 = off).</summary>
    public const byte RunInfoWearDetection = 0x0A;

    /// <summary>Set-device-info record type of the in-ear detection switch (same inverted value).</summary>
    public const byte DeviceInfoWearDetection = 0x06;

    /// <summary>Device-info record types.</summary>
    public const byte InfoName = 0x00, InfoFirmware = 0x01, InfoVidPid = 0x03, InfoBattery = 0x07;

    /// <summary>Status-push record types: battery <c>L R C</c> and the current noise mode.</summary>
    public const byte StatusBattery = 0x00, StatusNoiseMode = 0x04;

    /// <summary>Rejection detail when the find tone is refused because an earbud is in an ear.</summary>
    public const string FindWornDetail = "find-worn";

    /// <summary>Device config id of low-latency (game) mode; value 1 = on, 0 = off.</summary>
    public const ushort ConfigLowLatency = XiaomiConfig.LowLatency;

    /// <summary>Set-device-info payload for the in-ear detection switch.</summary>
    public static byte[] WearDetectionPayload(bool enabled) => [0x02, DeviceInfoWearDetection, enabled ? (byte)0 : (byte)1];

    /// <summary>
    /// Walks <c>[len][type][value(len-1)]</c> records (device info, run info, status pushes). A
    /// truncated tail ends the walk; records before it are kept.
    /// </summary>
    public static IReadOnlyList<(byte Type, byte[] Value)> ParseInfoRecords(ReadOnlySpan<byte> payload)
    {
        var records = new List<(byte, byte[])>();
        while (payload.Length >= 2)
        {
            int length = payload[0];
            if (length < 1 || payload.Length < length + 1) break;
            records.Add((payload[1], payload.Slice(2, length - 1).ToArray()));
            payload = payload[(length + 1)..];
        }
        return records;
    }

    /// <summary>
    /// One battery byte: level in bits 0–6, charging in bit 7, <c>0xFF</c> = not reported. Levels
    /// above 100 are treated as unknown.
    /// </summary>
    public static BatteryReading? DecodeBattery(byte value)
    {
        if (value == 0xFF) return null;
        int level = value & 0x7F;
        return level > 100 ? null : new BatteryReading(level, (value & 0x80) != 0);
    }

    /// <summary>Parses a device-info answer; false when it carries none of the records EncoDeck uses.</summary>
    public static bool TryParseTargetInfo(ReadOnlySpan<byte> payload, out XiaomiTargetInfo info)
    {
        string? name = null, firmware = null;
        int? vid = null, pid = null;
        BatteryReading?[]? battery = null;
        foreach (var (type, value) in ParseInfoRecords(payload))
        {
            switch (type)
            {
                case InfoName when value.Length > 0 && value.All(b => b is >= 0x20 and < 0x7F):
                    name = System.Text.Encoding.ASCII.GetString(value);
                    break;
                case InfoFirmware when value.Length >= 2:
                    firmware = $"{value[0] >> 4}.{value[0] & 0xF}.{value[1] >> 4}.{value[1] & 0xF}";
                    break;
                case InfoVidPid when value.Length == 4:
                    vid = (value[0] << 8) | value[1];
                    pid = (value[2] << 8) | value[3];
                    break;
                case InfoBattery when value.Length == 3:
                    battery = [DecodeBattery(value[0]), DecodeBattery(value[1]), DecodeBattery(value[2])];
                    break;
            }
        }
        info = new XiaomiTargetInfo(name, firmware, vid, pid, battery);
        return name is not null || firmware is not null || vid is not null || battery is not null;
    }
}

/// <summary>Device-info records: name, firmware (first earbud), VID/PID and left / right / case battery.</summary>
public sealed record XiaomiTargetInfo(string? Name, string? Firmware, int? VendorId, int? ProductId, BatteryReading?[]? Battery);

/// <summary>Device config ids (two-byte types of get / set / push config).</summary>
public static class XiaomiConfig
{
    /// <summary><c>[kind][left][right]</c> per gesture; a write sends one group with 0xFF for the untouched side.</summary>
    public const ushort Gestures = 0x0002;

    /// <summary>Automatically answer calls, 0 / 1.</summary>
    public const ushort AutoAnswer = 0x0003;

    /// <summary>Dual connection (multipoint), 0 / 1.</summary>
    public const ushort DualConnection = 0x0004;

    /// <summary>EQ preset id (10 = the custom curve).</summary>
    public const ushort EqualizerPreset = 0x0007;

    /// <summary>Find earbuds <c>[ring][1 left / 2 right / 3 both]</c> (write only).</summary>
    public const ushort FindEarbuds = 0x0009;

    /// <summary>Noise mode and strength <c>[mode][strength]</c>; mode 0 off, 1 ANC, 2 transparency.</summary>
    public const ushort Noise = 0x000B;

    /// <summary>Wear state (read / push only): bit 3 left in ear, 2 right in ear, 1 left in case, 0 right in case.</summary>
    public const ushort WearState = 0x000C;

    /// <summary>Spatial audio bit field: bit 0 on, bits 1–2 audio preference, bit 3 head tracking.</summary>
    public const ushort Spatial = 0x001D;

    /// <summary>Adaptive (smart) noise cancellation, 0 / 1.</summary>
    public const ushort AdaptiveAnc = 0x0025;

    /// <summary>Low latency (game mode), 0 / 1.</summary>
    public const ushort LowLatency = 0x002F;

    /// <summary>Custom EQ curve <c>[1][mode][max][min][id][nameLen][name][count]{freq16 gain}*</c>.</summary>
    public const ushort CustomEqualizer = 0x0037;

    /// <summary>Prompt volume <c>[level][default][max][1]</c>.</summary>
    public const ushort PromptVolume = 0x00A1;

    /// <summary>
    /// Pseudo id (never sent) recording that the run-info in-ear detection switch answered; it
    /// lives outside the config space, see <see cref="XiaomiProtocol.OpRunInfo"/>.
    /// </summary>
    internal const ushort WearDetectionSwitch = 0xF00A;
}
