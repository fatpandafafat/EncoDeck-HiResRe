// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Gesture, wear-detection switch and dual connection layouts ported from TWS-Pods-PC
// (https://github.com/Zhaoyi-ya/TWS-Pods-PC, 3350f42) and vivopods-mac
// (https://github.com/FxRayHughes/vivopods-mac, be25133), both GPL-3.0-only.
using System.Globalization;
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>One entry of the dual connection list, as reported (address in display order).</summary>
/// <param name="DeviceClass">Three bytes after the address, kept as reported.</param>
/// <param name="State">0 = not connected, 1 = connected, 2 = connected and playing; other values are kept unchanged.</param>
public sealed record VivoSourceDevice(byte[] Address, byte[] DeviceClass, byte State, string Name);

/// <summary>Pure decoders for vivo report payloads. Each returns false for malformed or failed reports.</summary>
public static class VivoParsers
{
    /// <summary>Double-tap report <c>[00][left][right]</c>. A shorter <c>0x8202</c> is the registration answer.</summary>
    public static bool TryParseDoubleTap(ReadOnlySpan<byte> p, out byte left, out byte right)
    {
        left = right = 0;
        if (p.Length < 3 || p[0] != 0) return false;
        left = p[1];
        right = p[2];
        return true;
    }

    /// <summary>Wear-detection switch <c>[00][0|1]</c>; any other state byte is not a switch report.</summary>
    public static bool TryParseWearDetection(ReadOnlySpan<byte> p, out bool enabled)
    {
        enabled = false;
        if (p.Length != 2 || p[0] != 0 || p[1] > 1) return false;
        enabled = p[1] == 1;
        return true;
    }

    /// <summary>Long-press function <c>[00][03][function]</c>.</summary>
    public static bool TryParseLongPress(ReadOnlySpan<byte> p, out byte function)
    {
        function = 0;
        if (p.Length < 3 || p[0] != 0 || p[1] != VivoConstants.LongPressSubCommand) return false;
        function = p[2];
        return true;
    }

    /// <summary>Long-press noise cycle <c>[00][05][left][right]</c>.</summary>
    public static bool TryParseNoiseCycle(ReadOnlySpan<byte> p, out byte left, out byte right)
    {
        left = right = 0;
        if (p.Length < 4 || p[0] != 0 || p[1] != VivoConstants.NoiseCycleSubCommand) return false;
        left = p[2];
        right = p[3];
        return true;
    }

    /// <summary>
    /// Dual connection list <c>[00][count]</c> followed by <c>[address:6][class:3][state][nameLength][name]</c>
    /// per device. A truncated entry rejects the whole report rather than showing a partial list.
    /// </summary>
    public static bool TryParseSourceDevices(ReadOnlySpan<byte> p, out IReadOnlyList<VivoSourceDevice> devices)
    {
        devices = [];
        if (p.Length < 2 || p[0] != 0) return false;
        var list = new List<VivoSourceDevice>(p[1]);
        int offset = 2;
        for (int i = 0; i < p[1]; i++)
        {
            if (offset + 11 > p.Length) return false;
            int nameLength = p[offset + 10];
            if (offset + 11 + nameLength > p.Length) return false;
            list.Add(new VivoSourceDevice(
                p.Slice(offset, 6).ToArray(),
                p.Slice(offset + 6, 3).ToArray(),
                p[offset + 9],
                Encoding.UTF8.GetString(p.Slice(offset + 11, nameLength))));
            offset += 11 + nameLength;
        }
        devices = list;
        return true;
    }

    /// <summary>
    /// Icon type (1 phone, 2 computer, 3 tablet, 0 unknown) from the three bytes after the address.
    /// They look like a Bluetooth class of device, most significant byte first (a MacBook reports
    /// <c>38 01 0C</c>, a phone <c>5A 02 0C</c>); this reading is an inference used for the icon only.
    /// </summary>
    public static int DeviceType(ReadOnlySpan<byte> deviceClass)
    {
        if (deviceClass.Length != 3) return 0;
        int major = deviceClass[1] & 0x1F;
        int minor = deviceClass[2] >> 2;
        return major switch
        {
            0x02 => 1,
            0x01 => minor == 7 ? 3 : 2,
            _ => 0,
        };
    }

    /// <summary>
    /// The PC's own adapter address from a Windows association endpoint id
    /// (<c>Bluetooth#Bluetooth{adapter}-{device}</c>), needed to recognise this PC in the list and to remove pairings.
    /// </summary>
    public static bool TryParseHostAddress(string deviceId, out byte[] address)
    {
        address = [];
        int hash = deviceId.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0) return false;
        var rest = deviceId.AsSpan(hash + 1);
        if (rest.StartsWith("BluetoothLE", StringComparison.OrdinalIgnoreCase)) rest = rest["BluetoothLE".Length..];
        else if (rest.StartsWith("Bluetooth", StringComparison.OrdinalIgnoreCase)) rest = rest["Bluetooth".Length..];
        else return false;
        if (rest.Length <= 17 || rest[17] != '-') return false;
        var text = rest[..17];
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (i > 0 && text[i * 3 - 1] != ':') return false;
            if (!byte.TryParse(text.Slice(i * 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return false;
        }
        address = bytes;
        return true;
    }

    /// <summary>
    /// <c>[00][left][right][case][charging]</c>; a level above 100 (0xFF) means the part is absent.
    /// Charging bits: 1 = left, 2 = right, 4 = case.
    /// </summary>
    public static bool TryParseBattery(ReadOnlySpan<byte> p, out BatteryReading? left, out BatteryReading? right, out BatteryReading? @case)
    {
        left = right = @case = null;
        if (p.Length < 5 || p[0] != 0) return false;
        left = Reading(p[1], (p[4] & 0x01) != 0);
        right = Reading(p[2], (p[4] & 0x02) != 0);
        @case = Reading(p[3], (p[4] & 0x04) != 0);
        return true;

        static BatteryReading? Reading(byte level, bool charging) => level <= 100 ? new BatteryReading(level, charging) : null;
    }

    /// <summary>Noise report <c>[00][mode][reduce level]…</c>; mode 0 = noise cancelling, 1 = off, 2 = transparency.</summary>
    public static bool TryParseNoise(ReadOnlySpan<byte> p, out NoiseControlMode mode)
    {
        mode = NoiseControlMode.Off;
        if (p.Length < 2 || p[0] != 0) return false;
        switch (p[1])
        {
            case VivoConstants.NoiseModeAnc: mode = NoiseControlMode.NoiseCancellation; return true;
            case VivoConstants.NoiseModeOff: mode = NoiseControlMode.Off; return true;
            case VivoConstants.NoiseModeTransparency: mode = NoiseControlMode.Transparency; return true;
            default: return false;
        }
    }

    public static byte NoiseModeByte(NoiseControlMode mode) => mode switch
    {
        NoiseControlMode.NoiseCancellation => VivoConstants.NoiseModeAnc,
        NoiseControlMode.Transparency => VivoConstants.NoiseModeTransparency,
        _ => VivoConstants.NoiseModeOff,
    };

    /// <summary>Wear report <c>[00][flags]</c>: bit0/bit1 = left/right in case, bit2/bit3 = left/right worn.</summary>
    public static bool TryParseWear(ReadOnlySpan<byte> p, out WearStatus wear)
    {
        wear = WearStatus.Unknown;
        if (p.Length < 2 || p[0] != 0) return false;
        byte f = p[1];
        wear = new WearStatus(
            LeftInEar: (f & 0x04) != 0,
            RightInEar: (f & 0x08) != 0,
            LeftInCase: (f & 0x01) != 0,
            RightInCase: (f & 0x02) != 0,
            CaseLidOpen: null);
        return true;
    }

    /// <summary>Status + value report <c>[00][value]…</c> (sound effect, game mode, find state).</summary>
    public static bool TryParseValue(ReadOnlySpan<byte> p, out byte value)
    {
        value = 0;
        if (p.Length < 2 || p[0] != 0) return false;
        value = p[1];
        return true;
    }

    /// <summary>
    /// Firmware report: either a compact binary version (<c>00 01 02 03 04 05 09 0C</c> → 2.5.9, bytes 2/5/6)
    /// or a status byte followed by text (<c>00 "1.2"</c>). A <c>_suffix</c> is dropped like the official app does.
    /// </summary>
    public static string? ParseFirmware(ReadOnlySpan<byte> p)
    {
        if (p.Length < 2 || p[0] != 0) return null;
        var body = p[1..];
        int printable = 0;
        foreach (byte b in body)
        {
            if (b is >= 0x20 and <= 0x7E) printable++;
        }
        if (p.Length >= 7 && printable * 2 < body.Length)
        {
            byte major = p[2], minor = p[5], patch = p[6];
            return major <= 99 && minor <= 99 && patch <= 99 ? $"{major}.{minor}.{patch}" : null;
        }
        int start = 0;
        while (start < body.Length && body[start] < 0x20) start++;
        int end = body[start..].IndexOf((byte)0);
        var textBytes = end < 0 ? body[start..] : body.Slice(start, end);
        if (textBytes.IsEmpty) return null;
        foreach (byte b in textBytes)
        {
            if (b is < 0x20 or > 0x7E) return null;
        }
        string text = Encoding.ASCII.GetString(textBytes).Trim();
        int underscore = text.IndexOf('_');
        return underscore >= 0 ? text[..underscore] : text;
    }
}
