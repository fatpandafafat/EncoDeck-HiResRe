// SPDX-License-Identifier: GPL-3.0-only
// Gesture and custom-EQ record layouts ported from BudsLink (https://github.com/maniacx/BudsLink,
// GPL-3.0), redmi-buds-bar by robin-liquidium (https://github.com/robin-liquidium/redmi-buds-bar,
// GPL-3.0), mebuds (https://github.com/paradox-ng/mebuds, GPL-3.0-or-later) and MiBudsClient
// (https://github.com/CesurPolat/MiBudsClient, GPL-3.0). Gesture kind / function ids also match
// android_packages_apps_XiaomiTWS (Apache-2.0) and OpenXia (MIT); see THIRD_PARTY_NOTICES.md.
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Touch gestures (config <see cref="XiaomiConfig.Gestures"/>): the value is a run of
/// <c>[kind][left][right]</c> groups; a write sends one group with 0xFF for the earbud left alone.
/// </summary>
public static class XiaomiGestures
{
    public const byte KindDouble = 1, KindTriple = 2, KindHold = 3, KindSingle = 4, KindSlide = 5;

    /// <summary>Function ids written per earbud.</summary>
    public const int VoiceAssistant = 0, PlayPause = 1, Previous = 2, Next = 3, VolumeUp = 4, VolumeDown = 5,
        NoiseControl = 6, None = 8, Volume = 11;

    /// <summary>"Leave this earbud unchanged" in a gesture write.</summary>
    public const byte Unchanged = 0xFF;

    /// <summary>Gestures in display order.</summary>
    public static IReadOnlyList<GestureAction> Actions { get; } =
        [GestureAction.SingleSqueeze, GestureAction.DoubleSqueeze, GestureAction.TripleSqueeze, GestureAction.SqueezeAndHold, GestureAction.Slide];

    public static GestureAction? ActionOf(byte kind) => kind switch
    {
        KindSingle => GestureAction.SingleSqueeze,
        KindDouble => GestureAction.DoubleSqueeze,
        KindTriple => GestureAction.TripleSqueeze,
        KindHold => GestureAction.SqueezeAndHold,
        KindSlide => GestureAction.Slide,
        _ => null,
    };

    public static byte? KindOf(GestureAction action) => action switch
    {
        GestureAction.SingleSqueeze => KindSingle,
        GestureAction.DoubleSqueeze => KindDouble,
        GestureAction.TripleSqueeze => KindTriple,
        GestureAction.SqueezeAndHold => KindHold,
        GestureAction.Slide => KindSlide,
        _ => null,
    };

    /// <summary>
    /// Functions offered for a gesture: the set every real-device report agrees on. Noise control
    /// on hold only when the earbuds' noise control is confirmed. Other values the earbuds report
    /// are shown as unknown and never written.
    /// </summary>
    public static IReadOnlyList<int> Functions(GestureAction action, bool noiseControl) => action switch
    {
        GestureAction.SqueezeAndHold => noiseControl ? [None, VoiceAssistant, NoiseControl] : [None, VoiceAssistant],
        GestureAction.Slide => [None, Volume],
        _ => [None, PlayPause, Previous, Next, VolumeUp, VolumeDown],
    };

    /// <summary>
    /// Controls-page rows: every gesture on each earbud, shown once the earbuds reported it. Noise
    /// control on hold is listed but selectable only after the earbuds' noise control is confirmed.
    /// </summary>
    public static IReadOnlyList<BrandGestureSlot> Slots { get; } =
    [
        .. from side in new[] { EarSide.Left, EarSide.Right }
           from action in Actions
           select new BrandGestureSlot(side, 0, action, SlotLabelKey(action),
               Functions(action, noiseControl: true)
                   .Select(f => new BrandGestureChoice(f, [LabelKey(f)], f == NoiseControl ? DeviceCapability.NoiseCancellation : null))
                   .ToArray()),
    ];

    private static string SlotLabelKey(GestureAction action) => action switch
    {
        GestureAction.SingleSqueeze => "Gesture_Tap1",
        GestureAction.DoubleSqueeze => "Gesture_Tap2",
        GestureAction.TripleSqueeze => "Gesture_Tap3",
        GestureAction.SqueezeAndHold => "Gesture_TapHold",
        _ => "Gesture_Slide",
    };

    /// <summary>Localization key of a function label (shared gesture strings).</summary>
    public static string LabelKey(int function) => function switch
    {
        None => "Function_0",
        VoiceAssistant => "Function_3",
        PlayPause => "Function_1",
        Previous => "Function_5",
        Next => "Function_6",
        VolumeUp => "Function_11",
        VolumeDown => "Function_12",
        NoiseControl => "Function_8",
        Volume => "Function_7",
        _ => "",
    };

    /// <summary>Parses the gesture value into raw groups; false for a partial or duplicated group.</summary>
    public static bool TryParse(ReadOnlySpan<byte> value, out IReadOnlyDictionary<byte, (byte Left, byte Right)> groups)
    {
        var result = new Dictionary<byte, (byte, byte)>();
        groups = result;
        if (value.Length == 0 || value.Length % 3 != 0) return false;
        for (int i = 0; i < value.Length; i += 3)
        {
            if (!result.TryAdd(value[i], (value[i + 1], value[i + 2]))) return false;
        }
        return true;
    }

    /// <summary>The per-earbud assignments of the gestures EncoDeck knows (unknown kinds are left out).</summary>
    public static IReadOnlyList<KeyFunction> ToKeyFunctions(IReadOnlyDictionary<byte, (byte Left, byte Right)> groups) =>
    [
        .. from g in groups
           let action = ActionOf(g.Key)
           where action is not null
           from pair in new[] { (EarSide.Left, g.Value.Left), (EarSide.Right, g.Value.Right) }
           select new KeyFunction(pair.Item1, 0, action.Value, pair.Item2),
    ];

    /// <summary>The gesture write: one group, 0xFF for the other earbud.</summary>
    public static byte[] WriteValue(byte kind, EarSide side, int function) =>
        [kind, side == EarSide.Left ? (byte)function : Unchanged, side == EarSide.Right ? (byte)function : Unchanged];
}

/// <summary>
/// The custom EQ curve (config <see cref="XiaomiConfig.CustomEqualizer"/>):
/// <c>[1][mode][max dB][min dB][id][nameLen][name][count]{frequency BE16, gain}*</c>, gains
/// sign-magnitude (bit 7 = negative). It is the EQ preset with id <see cref="PresetId"/>.
/// </summary>
public sealed record XiaomiCustomCurve(int MaxGain, int MinGain, string Name, IReadOnlyList<EqualizerBand> Bands)
{
    /// <summary>EQ preset id of the custom curve.</summary>
    public const int PresetId = 10;

    public static bool TryParse(ReadOnlySpan<byte> value, out XiaomiCustomCurve curve)
    {
        curve = null!;
        if (value.Length < 7 || value[0] != 1) return false;
        int max = value[2], min = value[3];
        if (max is 0 or > 24 || min is 0 or > 24) return false;
        int nameLength = value[5];
        int index = 6 + nameLength;
        if (index >= value.Length) return false;
        var nameBytes = value.Slice(6, nameLength);
        foreach (byte b in nameBytes) if (b is < 0x20 or >= 0x7F) return false;
        int count = value[index++];
        if (count is 0 or > 32 || value.Length != index + (3 * count)) return false;
        var bands = new EqualizerBand[count];
        for (int i = 0; i < count; i++, index += 3)
        {
            int frequency = (value[index] << 8) | value[index + 1];
            int gain = DecodeGain(value[index + 2]);
            if (frequency == 0 || gain > max || gain < -min) return false;
            bands[i] = new EqualizerBand(frequency, gain);
        }
        curve = new XiaomiCustomCurve(max, -min, System.Text.Encoding.ASCII.GetString(nameBytes), bands);
        return true;
    }

    /// <summary>The write that stores the curve: header <c>01 0A 01 01 01 00 count</c>, then the bands.</summary>
    public static byte[] SaveValue(IReadOnlyList<EqualizerBand> bands)
    {
        var value = new List<byte> { 0x01, PresetId, 0x01, 0x01, 0x01, 0x00, (byte)bands.Count };
        foreach (var band in bands)
        {
            value.Add((byte)(band.FrequencyHz >> 8));
            value.Add((byte)band.FrequencyHz);
            value.Add(EncodeGain(band.Gain));
        }
        return [.. value];
    }

    public static int DecodeGain(byte value) => (value & 0x80) != 0 ? -(value & 0x7F) : value;

    public static byte EncodeGain(int gain) => gain < 0 ? (byte)(0x80 | -gain) : (byte)gain;

    public bool SameBands(IReadOnlyList<EqualizerBand> other) =>
        Bands.Count == other.Count && Bands.Zip(other).All(p => p.First.FrequencyHz == p.Second.FrequencyHz && p.First.Gain == p.Second.Gain);
}
