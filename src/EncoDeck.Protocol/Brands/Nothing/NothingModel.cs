// SPDX-License-Identifier: GPL-3.0-only
// Ported from HeyBuds (https://github.com/abaichuan1017/HeyBuds) 59e3bb86e53d7ff15a97223287eda61f335404a6, GPL-3.0-or-later.
// Model, payload and identity facts from swift-nothing-ear, nothing-x-macos (GPL-3.0), EarA-linux (GPL-3.0-or-later),
// omarchy-headphones (MIT) and openNothing (protocol facts only), as attributed in HeyBuds docs/licenses/Nothing.md.
using System.Text;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Nothing;

public enum NothingModelKind
{
    EarA,
    Headphone1,
    CmfBuds2,
    CmfBudsPro2,
}

/// <summary>
/// The four exact Nothing / CMF profiles. A name only selects a candidate; the identity read must
/// still return this model's code before anything else is used.
/// </summary>
public sealed class NothingModel
{
    /// <summary>Dedicated RFCOMM control service; its channel comes from SDP, never a fixed number.</summary>
    public static readonly Guid ServiceId = new("AEAC4A03-DFF5-498F-843A-34487CF133EB");

    public static IReadOnlyList<NothingModel> All { get; } =
    [
        new(NothingModelKind.EarA, "nothing-ear-a", "B162", "Nothing Ear (a)", ["Nothing Ear (a)", "Nothing Ear(a)", "Nothing Ear a", "Ear (a)"]),
        new(NothingModelKind.Headphone1, "nothing-headphone-1", "B170", "Nothing Headphone (1)", ["Nothing Headphone (1)", "Nothing Headphone(1)", "Nothing Headphone 1", "Headphone (1)"]),
        new(NothingModelKind.CmfBuds2, "nothing-cmf-buds-2", "B179", "CMF Buds 2", ["CMF Buds 2", "CMF Buds2"]),
        new(NothingModelKind.CmfBudsPro2, "nothing-cmf-buds-pro-2", "B172", "CMF Buds Pro 2", ["CMF Buds Pro 2", "CMF Buds Pro2"]),
    ];

    private NothingModel(NothingModelKind kind, string modelId, string modelCode, string displayName, string[] aliases)
    {
        Kind = kind;
        ModelId = modelId;
        ModelCode = modelCode;
        DisplayName = displayName;
        Aliases = aliases;
    }

    public NothingModelKind Kind { get; }
    public string ModelId { get; }
    public string ModelCode { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> Aliases { get; }

    public bool SingleBattery => Kind == NothingModelKind.Headphone1;

    /// <summary>B172 checks only the payload; the others the whole frame. One fixed policy, no fallback.</summary>
    public bool PayloadOnlyCrc => Kind == NothingModelKind.CmfBudsPro2;

    private bool GenrePresets => Kind is NothingModelKind.CmfBuds2 or NothingModelKind.CmfBudsPro2;

    /// <summary>Exact alias, ignoring case and surrounding whitespace; no prefix or suffix matching.</summary>
    public static NothingModel? Named(string? name)
    {
        string candidate = (name ?? "").Trim();
        return All.FirstOrDefault(m => m.Aliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)));
    }

    public NothingRequest IdentityRead => Kind == NothingModelKind.CmfBudsPro2 ? new(0xC01C, 0x401C) : new(0xC006, 0x4006);
    public NothingRequest FirmwareRead { get; } = new(0xC042, 0x4042);
    public NothingRequest BatteryRead { get; } = new(0xC007, 0x4007);
    public NothingRequest NoiseRead => new(0xC01E, Kind == NothingModelKind.CmfBudsPro2 ? [3] : [], 0x401E);
    public NothingRequest EqRead => GenrePresets ? new(0xC050, 0x4050) : new(0xC01F, 0x401F);

    /// <summary>Headphone (1) only: Advanced EQ can override the preset register.</summary>
    public NothingRequest? AdvancedEqRead => Kind == NothingModelKind.Headphone1 ? new(0xC04C, 0x404C) : null;

    public IReadOnlyList<byte> PresetIds => GenrePresets ? [0, 1, 2, 3, 4, 5] : [0, 1, 2, 3];

    private static readonly string[] VoiceLabels = ["Balanced", "Voice", "More Treble", "More Bass"];
    private static readonly string[] GenreLabels = ["Balanced", "Rock", "Electronic", "Pop", "Enhance Vocals", "Classical"];

    public string? PresetLabel(byte id)
    {
        var labels = GenrePresets ? GenreLabels : VoiceLabels;
        return id < labels.Length ? labels[id] : null;
    }

    /// <summary>Wire mode values: 1 high ANC, 2 medium, 3 low, 4 adaptive, 5 off, 7 transparency.</summary>
    public static NothingRequest? NoiseWrite(byte mode) => mode is >= 1 and <= 5 or 7 ? new(0xF00F, [1, mode, 0], null) : null;

    public NothingRequest? EqWrite(byte id) => PresetIds.Contains(id) ? new(GenrePresets ? (ushort)0xF01D : (ushort)0xF010, [id, 0], null) : null;

    /// <summary>
    /// A complete battery report. Absent and unavailable components are omitted; a real 0% stays a
    /// reading. Bit 7 is charging. Returns null for a malformed report.
    /// </summary>
    public IReadOnlyDictionary<BatteryComponent, BatteryReading>? ParseBattery(ReadOnlySpan<byte> payload)
    {
        static BatteryReading? Reading(byte raw) => (raw & 0x7F) <= 100 ? new BatteryReading(raw & 0x7F, (raw & 0x80) != 0) : null;
        var result = new Dictionary<BatteryComponent, BatteryReading>();
        if (SingleBattery)
        {
            // Swift fixture [0,0,value]; RFCOMM profile [count=1,id=6,value].
            if (payload.Length != 3 || !(payload[0] == 0 && payload[1] == 0 || payload[0] == 1 && payload[1] == 6)) return null;
            if (Reading(payload[2]) is { } single) result[BatteryComponent.Left] = single;
            return result;
        }
        if (payload.Length == 0 || payload[0] > 3 || payload.Length != 1 + payload[0] * 2) return null;
        var seen = new HashSet<byte>();
        for (int offset = 1; offset < payload.Length; offset += 2)
        {
            byte id = payload[offset];
            if (!seen.Add(id)) return null;
            // B172's BLE fixture uses case id 4 but its RFCOMM reference names 5, and its capture has
            // buds only. Keep verified bud readings without guessing a case mapping.
            if (Kind == NothingModelKind.CmfBudsPro2 && id is 4 or 5) continue;
            BatteryComponent component;
            switch (id)
            {
                case 2: component = BatteryComponent.Left; break;
                case 3: component = BatteryComponent.Right; break;
                case 4: component = BatteryComponent.Case; break;
                default: return null;
            }
            if (Reading(payload[offset + 1]) is { } reading) result[component] = reading;
        }
        return result;
    }

    /// <summary>The raw mode value, or null for a malformed / unsourced layout.</summary>
    public byte? ParseNoise(ReadOnlySpan<byte> payload)
    {
        byte value;
        if (payload.Length == 2 && Kind != NothingModelKind.CmfBudsPro2)
        {
            if (payload[0] != 0) return null;
            value = payload[1];
        }
        else
        {
            // (kind,value,0) groups: kind 1 is the mode, kind 2 the ANC level. B172 puts the level
            // group first, so the mode is never assumed to be the first value. B170 has only the
            // compact status as evidence.
            if (Kind == NothingModelKind.Headphone1 || payload.Length is not (3 or 6)) return null;
            byte? mode = null;
            var seen = new HashSet<byte>();
            for (int offset = 0; offset < payload.Length; offset += 3)
            {
                byte kind = payload[offset], setting = payload[offset + 1];
                if (payload[offset + 2] != 0 || !seen.Add(kind)) return null;
                if (kind == 1) mode = setting;
                else if (kind != 2 || setting is < 1 or > 4) return null;
            }
            if (mode is null) return null;
            value = mode.Value;
        }
        return value is >= 1 and <= 5 or 7 ? value : null;
    }

    /// <summary>All four model fixtures use one byte; the generic two-byte fallback is not imported.</summary>
    public byte? ParseEq(ReadOnlySpan<byte> payload) => payload.Length == 1 && PresetIds.Contains(payload[0]) ? payload[0] : null;

    public bool? ParseAdvancedEq(ReadOnlySpan<byte> payload) =>
        AdvancedEqRead is not null && payload.Length == 1 && payload[0] <= 1 ? payload[0] == 1 : null;

    /// <summary>Dotted numeric ASCII version (2–4 parts) with optional trailing zero padding.</summary>
    public static string? ParseFirmware(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > 32) return null;
        int length = payload.IndexOf((byte)0);
        if (length < 0) length = payload.Length;
        if (payload[length..].ContainsAnyExcept((byte)0)) return null;
        foreach (byte b in payload[..length])
            if (b > 0x7F) return null;
        string version = Encoding.ASCII.GetString(payload[..length]);
        return VersionParts(version) is null ? null : version;
    }

    private static ushort[]? VersionParts(string? version)
    {
        if (version is null || version.Length > 31) return null;
        string[] parts = version.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var result = new ushort[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length is 0 or > 5 || !part.All(char.IsAsciiDigit) || !ushort.TryParse(part, out result[i])) return null;
        }
        return result;
    }

    /// <summary>
    /// CMF Buds 2 presets require four-part firmware of at least 1.0.1.50 (a conservative gate, not
    /// a verified universal minimum). Battery and noise do not depend on it; other models have no gate.
    /// </summary>
    public bool AllowsEq(string? firmware)
    {
        if (Kind != NothingModelKind.CmfBuds2) return true;
        if (VersionParts(firmware) is not { Length: 4 } parts) return false;
        ushort[] minimum = [1, 0, 1, 50];
        for (int i = 0; i < 4; i++)
            if (parts[i] != minimum[i]) return parts[i] > minimum[i];
        return true;
    }

    /// <summary>
    /// The model code from the identity reply. Serial and address fields are discarded at once;
    /// conflicting or unknown SKU entries fail closed and nothing falls back to the device name.
    /// </summary>
    public string? IdentityCode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > 1024) return null;
        if (Kind == NothingModelKind.CmfBudsPro2)
        {
            int length = payload.IndexOf((byte)0);
            if (length < 0) length = payload.Length;
            if (length != 4 || payload[4..].ContainsAnyExcept((byte)0)) return null;
            string text = Encoding.ASCII.GetString(payload[..4]);
            return All.Any(m => m.ModelCode == text) ? text : null;
        }
        // Configuration text may carry a one-byte record-count prefix.
        var bytes = payload;
        if (bytes[0] is >= 1 and <= 16 and not 10 and not 13) bytes = bytes[1..];
        if (bytes.Length > 0 && bytes[^1] == 0) bytes = bytes[..^1];
        if (bytes.Length == 0) return null;
        foreach (byte b in bytes)
            if (b is not (10 or 13) and (< 32 or > 126)) return null;
        string? code = null;
        foreach (string rawLine in Encoding.ASCII.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            string[] fields = line.Split(',');
            if (fields.Length != 3 || fields[0].Length == 0 || fields[1].Length == 0
                || !fields[0].All(char.IsAsciiDigit) || !fields[1].All(char.IsAsciiDigit)) return null;
            if (fields[1] != "4") continue; // type 4 = serial / SKU
            if (CodeFromSku(fields[2]) is not { } found || code is not null && code != found) return null;
            code = found;
        }
        return code;
    }

    private static string? CodeFromSku(string value)
    {
        if (value.Length is < 8 or > 64 || !value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'Z')) return null;
        if (value.StartsWith("M3", StringComparison.Ordinal))
        {
            // Three-digit SKU at offset 3.
            string sku = value.Substring(3, 3);
            return sku is "603" or "606" ? "B170" : null;
        }
        if (!value.StartsWith("SH", StringComparison.Ordinal) && !value.StartsWith("13", StringComparison.Ordinal)) return null;
        string digits = value.Substring(4, 2);
        if (!digits.All(char.IsAsciiDigit)) return null;
        // nothing-x-macos' 84/85/86 → B179 mapping conflicts with the Swift source and is not used.
        return int.Parse(digits) switch
        {
            >= 63 and <= 68 or >= 71 and <= 73 => "B162",
            >= 76 and <= 83 => "B172",
            98 or 99 => "B179",
            _ => null,
        };
    }

    /// <summary>Commands this model's receiver accepts: its read responses, events and write ACKs.</summary>
    public bool Accepts(ushort command)
    {
        if (command == IdentityRead.ResponseCommand || command == FirmwareRead.ResponseCommand || command == BatteryRead.ResponseCommand
            || command == NoiseRead.ResponseCommand || command == EqRead.ResponseCommand || command == AdvancedEqRead?.ResponseCommand) return true;
        return command is 0xE001 or 0xE003 or 0x700F || command == (GenrePresets ? 0x701D : 0x7010);
    }

    /// <summary>Only B172 sends CRC-free (control 0100) events, and only E001/E003 with sequence 0.</summary>
    public bool IsCrcFreeEvent(ushort command, byte sequence) =>
        Kind == NothingModelKind.CmfBudsPro2 && sequence == 0 && command is 0xE001 or 0xE003;

    public override string ToString() => ModelId;
}
