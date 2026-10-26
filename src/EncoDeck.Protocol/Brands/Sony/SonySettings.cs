// SPDX-License-Identifier: GPL-3.0-only
// Command ids, inquired types and payload layouts of the MDR settings below follow
// mos9527/SonyHeadphonesClient (libmdr), MIT License, Copyright (c) 2026 mos9527, Amr Satrio and
// other contributors (revision 4061cf353fd0044e559e9a1f1b7f2faa334d09c6). Ported to C#; see
// THIRD_PARTY_NOTICES.md section 3 for the licence text.
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>A boolean MDR setting: query <c>[Get, Type]</c>, reply <c>[Get+1, Type, …]</c>, write <c>[Set, …]</c>.</summary>
internal sealed record SonyBoolSetting(DeviceToggle Toggle, byte Get, byte Type, byte Set, Func<byte[], bool?> Parse, Func<bool, byte[]> Payload)
{
    /// <summary>Reply and notification commands carry the same layout.</summary>
    public bool Matches(ReadOnlySpan<byte> p) => p.Length >= 2 && (p[0] == Get + 1 || p[0] == Get + 3) && p[1] == Type;
}

/// <summary>Per-model MDR function tables and setting layouts (v1 = protocol 1, v2 = protocol 2).</summary>
internal static class SonySettings
{
    // Table 1 function ids.
    public const byte V1BatteryLevel = 0x11, V1LeftRightBattery = 0x15, V1CradleBattery = 0x18, V1PresetEq = 0x51,
        V1NoiseAndAmbient = 0x62, V1Upscaling = 0xE2, V1ControlByWearing = 0xF3, V1AutoPowerOff = 0xF4, V1SmartTalking = 0xF5;
    public const byte V2AutoPowerOff = 0x24, V2AutoPowerOffWearing = 0x25, V2Upscaling = 0xE2, V2PlaybackByWearing = 0xF1,
        V2AssignableSetting = 0xF3, V2SmartTalkingType2 = 0xFC;

    /// <summary>Function ids of a <c>CONNECT_RET_SUPPORT_FUNCTION</c> (0x07) reply: one byte each in v1, (id, priority) pairs in v2.</summary>
    public static bool TryFunctions(ReadOnlySpan<byte> p, bool v2, out HashSet<byte> functions)
    {
        functions = [];
        if (p.Length < 3 || p[0] != 7 || p[1] != 0 || p.Length != 3 + p[2] * (v2 ? 2 : 1)) return false;
        for (int i = 3; i < p.Length; i += v2 ? 2 : 1) functions.Add(p[i]);
        return true;
    }

    /// <summary><c>CONNECT_RET_DEVICE_INFO</c> string (model name 1 / firmware 2): <c>[05, type, len, ascii…]</c>.</summary>
    public static string? TryDeviceInfo(ReadOnlySpan<byte> p, byte type)
    {
        if (p.Length < 3 || p[0] != 5 || p[1] != type || p.Length != 3 + p[2] || p[2] == 0) return null;
        string text = Encoding.ASCII.GetString(p[3..]);
        return text.All(c => c is >= ' ' and <= '~') ? text : null;
    }

    public static IReadOnlyList<SonyBoolSetting> BooleanSettings(bool v2, IReadOnlySet<byte> functions)
    {
        List<SonyBoolSetting> settings = [];
        if (!v2)
        {
            // AUDIO type 2 UPSCALING: [type, AUTO_OFF, value], value 1 = auto (on).
            if (functions.Contains(V1Upscaling))
                settings.Add(new(DeviceToggle.Upscaling, 0xE6, 2, 0xE8, p => p is [_, 2, 0, 0 or 1] ? p[3] == 1 : null, on => [2, 0, (byte)(on ? 1 : 0)]));
            // SYSTEM type 3 CONTROL_BY_WEARING, written with SYSTEM_SET_EXTENDED_PARAM as libmdr does.
            if (functions.Contains(V1ControlByWearing))
                settings.Add(new(DeviceToggle.InEarDetection, 0xF6, 3, 0xFC, p => p is [_, 3, 0, 0 or 1] ? p[3] == 1 : null, on => [3, 0, (byte)(on ? 1 : 0)]));
            // SYSTEM type 5 SMART_TALKING_MODE: kind 2 is an active talking session, which is still "on".
            if (functions.Contains(V1SmartTalking))
                settings.Add(new(DeviceToggle.SpeakToChat, 0xF6, 5, 0xF8, p => p is [_, 5, _, 0 or 1] ? p[2] == 2 || p[3] == 1 : null, on => [5, 1, (byte)(on ? 1 : 0)]));
        }
        else
        {
            // v2 on/off values are inverted: 0 = ON, 1 = OFF.
            if (functions.Contains(V2Upscaling))
                settings.Add(new(DeviceToggle.Upscaling, 0xE6, 1, 0xE8, p => p is [_, 1, 0 or 1] ? p[2] == 1 : null, on => [1, (byte)(on ? 1 : 0)]));
            if (functions.Contains(V2PlaybackByWearing))
                settings.Add(new(DeviceToggle.InEarDetection, 0xF6, 1, 0xF8, p => p is [_, 1, 0 or 1] ? p[2] == 0 : null, on => [1, (byte)(on ? 0 : 1)]));
            if (functions.Contains(V2SmartTalkingType2))
                settings.Add(new(DeviceToggle.SpeakToChat, 0xF6, 0x0C, 0xF8, p => p is [_, 0x0C, 0 or 1, 0 or 1] ? p[2] == 0 : null, on => [0x0C, (byte)(on ? 0 : 1), 1]));
        }
        return settings;
    }

    /// <summary>
    /// Boolean general setting from <c>GENERAL_SETTING_RET_CAPABILITY</c>: the setting's enum name, or null
    /// for list settings and raw (localized) names.
    /// </summary>
    public static string? TryGeneralSettingName(ReadOnlySpan<byte> p, bool v2, byte type)
    {
        if (p.Length < 5 || p[0] != 0xD1 || p[1] != type) return null;
        // v2: [D1, type, settingType(0 bool), format(1 enum), subject, summary]
        // v1: [D1, type, format(2 enum), subject, summary, settingType(1 bool)]
        int offset = v2 ? 4 : 3;
        if (v2 ? p[2] != 0 || p[3] != 1 : p[2] != 2) return null;
        if (!TryString(p, ref offset, out string subject) || !TryString(p, ref offset, out _)) return null;
        if (v2 ? offset != p.Length : offset != p.Length - 1 || p[^1] != 1) return null;
        return subject;
    }

    private static bool TryString(ReadOnlySpan<byte> p, ref int offset, out string value)
    {
        value = "";
        if (offset >= p.Length || offset + 1 + p[offset] > p.Length) return false;
        var bytes = p.Slice(offset + 1, p[offset]);
        foreach (byte b in bytes) if (b is < 0x20 or > 0x7E) return false;
        value = Encoding.ASCII.GetString(bytes);
        offset += 1 + p[offset];
        return true;
    }

    public static DeviceToggle? GeneralSettingToggle(string name) => name switch
    {
        "TOUCH_PANEL_SETTING" => DeviceToggle.TouchControls,
        "MULTIPOINT_SETTING" => DeviceToggle.DualDeviceConnection,
        _ => null,
    };

    /// <summary>Boolean general setting value: <c>[D7|D9, type, settingType, value]</c>; v1 ON = 1, v2 ON = 0.</summary>
    public static bool? TryGeneralSettingValue(ReadOnlySpan<byte> p, bool v2, byte type) =>
        p.Length == 4 && p[0] is 0xD7 or 0xD9 && p[1] == type && p[2] == (v2 ? 0 : 1) && p[3] <= 1 ? p[3] == (v2 ? 0 : 1) : null;

    public static byte[] GeneralSettingPayload(bool v2, byte type, bool on) => [type, (byte)(v2 ? 0 : 1), (byte)(on == v2 ? 0 : 1)];

    // ---- auto power off --------------------------------------------------------------------------

    public const byte AutoOffDisabled = 0x11, AutoOffWhenRemoved = 0x10;

    /// <summary>Element id → minutes. v1 has no 15-minute element (libmdr / WH-1000XM3 4.5.2).</summary>
    public static int? AutoOffMinutes(byte element, bool v2) => element switch
    {
        0 => 5, 1 => 30, 2 => 60, 3 => 180, 4 when v2 => 15, _ => null,
    };

    public static bool IsAutoOffElement(byte element, bool v2, bool wearing) =>
        AutoOffMinutes(element, v2) is not null || element == AutoOffDisabled || (element == AutoOffWhenRemoved && (wearing || !v2));

    /// <summary>Capability reply <c>[F1|21, type, n, elements…]</c>.</summary>
    public static IReadOnlyList<byte>? TryAutoOffCapability(ReadOnlySpan<byte> p, byte command, byte type)
    {
        if (p.Length < 3 || p[0] != command || p[1] != type || p.Length != 3 + p[2] || p[2] == 0) return null;
        return p[3..].ToArray();
    }

    public static DeviceOptionChoice AutoOffChoice(byte element, bool v2) => element switch
    {
        AutoOffDisabled => new(element, "AutoOff_Off"),
        AutoOffWhenRemoved => new(element, "AutoOff_WhenRemoved"),
        _ => AutoOffMinutes(element, v2) is { } minutes ? new(element, "AutoOff_Minutes", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new(element, "SonyOption_Unknown", element.ToString("X2")),
    };

    // ---- assignable keys (v2) ---------------------------------------------------------------------

    internal sealed record AssignableKey(byte Key, byte Type, IReadOnlyList<byte> Presets);

    /// <summary>
    /// <c>SYSTEM_RET_CAPABILITY</c> for ASSIGNABLE_SETTINGS (v2): keys, each with its selectable presets.
    /// Each preset lists its counts first and then its actions (libmdr custom serialization).
    /// </summary>
    public static IReadOnlyList<AssignableKey>? TryAssignableCapability(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3 || p[0] != 0xF1 || p[1] != 3 || p[2] == 0) return null;
        int offset = 3;
        List<AssignableKey> keys = [];
        for (int k = 0; k < p[2]; k++)
        {
            if (offset + 4 > p.Length) return null;
            byte key = p[offset], type = p[offset + 1];
            int presetCount = p[offset + 3];
            offset += 4;
            List<byte> presets = [];
            for (int i = 0; i < presetCount; i++)
            {
                if (offset + 3 > p.Length) return null;
                byte preset = p[offset];
                int actions = p[offset + 1], customizable = p[offset + 2];
                if (actions == 0 && customizable == 0) return null;
                offset += 3 + actions * 2;
                for (int c = 0; c < customizable; c++)
                {
                    if (offset + 3 > p.Length) return null;
                    offset += 3 + p[offset + 2];
                }
                if (offset > p.Length || presets.Contains(preset)) return null;
                presets.Add(preset);
            }
            if (presets.Count == 0) return null;
            keys.Add(new(key, type, presets));
        }
        return offset == p.Length ? keys : null;
    }

    /// <summary><c>[F7|F9, 03, n, presets…]</c>.</summary>
    public static byte[]? TryAssignablePresets(ReadOnlySpan<byte> p, int keys) =>
        p.Length == 3 + keys && p[0] is 0xF7 or 0xF9 && p[1] == 3 && p[2] == keys ? p[3..].ToArray() : null;

    public static string KeyLabel(byte key) => key switch
    {
        0 => "SonyKey_Left",
        1 => "SonyKey_Right",
        2 => "SonyKey_Custom",
        4 or 5 => "SonyKey_NcAmbient",
        _ => "SonyKey_Other",
    };

    private static readonly HashSet<byte> NamedPresets = [0x00, 0x10, 0x20, 0x21, 0x30, 0x31, 0x32, 0x35, 0x36, 0x45, 0x46, 0x47, 0xFF];

    public static DeviceOptionChoice PresetChoice(byte preset) => NamedPresets.Contains(preset)
        ? new(preset, $"SonyPreset_{preset:X2}")
        : new(preset, "SonyOption_Unknown", preset.ToString("X2"));

    // ---- equalizer -------------------------------------------------------------------------------

    /// <summary>
    /// Labels for preset ids whose device-supplied name is empty (WF-LC900 2.0.3 and WH-1000XM3 send
    /// none; the LE Audio link carries ids only). The user slots 0xA0–0xA5 are named after the Sony
    /// app generation: "Custom" / "User Setting N" on MDR v1 (<paramref name="legacy"/>), "Manual" /
    /// "Custom N" on v2 (as WH-1000XM6 shows them). Keys with ":n" are format strings with one argument.
    /// </summary>
    public static (string Key, string Name)? PresetLabel(byte id, bool legacy = false) => id switch
    {
        0x00 => ("SonyEq_Off", "Off"),
        0x01 => ("SonyEq_Rock", "Rock"),
        0x02 => ("SonyEq_Pop", "Pop"),
        0x03 => ("SonyEq_Jazz", "Jazz"),
        0x04 => ("SonyEq_Dance", "Dance"),
        0x05 => ("SonyEq_Edm", "EDM"),
        0x06 => ("SonyEq_RnbHipHop", "R&B/Hip-Hop"),
        0x07 => ("SonyEq_Acoustic", "Acoustic"),
        0x10 => ("SonyEq_Bright", "Bright"),
        0x11 => ("SonyEq_Excited", "Excited"),
        0x12 => ("SonyEq_Mellow", "Mellow"),
        0x13 => ("SonyEq_Relaxed", "Relaxed"),
        0x14 => ("SonyEq_Vocal", "Vocal"),
        0x15 => ("SonyEq_TrebleBoost", "Treble Boost"),
        0x16 => ("SonyEq_BassBoost", "Bass Boost"),
        0x17 => ("SonyEq_Speech", "Speech"),
        0x30 => ("SonyEq_Heavy", "Heavy"),
        0x31 => ("SonyEq_Clear", "Clear"),
        0x32 => ("SonyEq_Hard", "Hard"),
        0x33 => ("SonyEq_Soft", "Soft"),
        0x20 => ("SonyEq_Game", "Game"),
        0xA0 when legacy => ("SonyEq_Custom", "Custom"),
        0xA0 => ("SonyEq_Manual", "Manual"),
        >= 0xA1 and <= 0xA5 when legacy => ($"SonyEq_UserSetting:{id - 0xA0}", $"User Setting {id - 0xA0}"),
        >= 0xA1 and <= 0xA5 => ($"SonyEq_CustomN:{id - 0xA0}", $"Custom {id - 0xA0}"),
        _ => null,
    };

    /// <summary>
    /// WH-1000XM6 (firmware 3.0.0 and later) only applies Heavy/Clear/Hard/Soft (0x30–0x33), Custom
    /// (0xA0) and the user settings (mos9527/SonyHeadphonesClient PR #48); other advertised ids are hidden.
    /// </summary>
    public static bool IsPresetAccepted(string model, byte id) =>
        model != "WH-1000XM6" || id is >= 0x30 and <= 0x33 or >= 0xA0 and <= 0xA5;

    // ---- firmware quirks -------------------------------------------------------------------------

    /// <summary>
    /// WF-C700N firmware 2.0.5 acknowledges NC/ASM writes and echoes the requested state but stays in
    /// its previous mode; 2.0.7 fixes it (mos9527/SonyHeadphonesClient device notes).
    /// </summary>
    public static bool IgnoresNoiseWrites(string model, string? firmware) => model == "WF-C700N" && firmware == "2.0.5";
}
