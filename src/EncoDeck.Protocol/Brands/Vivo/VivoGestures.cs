// SPDX-License-Identifier: GPL-3.0-only
// Gesture codes ported from TWS-Pods-PC vivo/PROTOCOL.md §4.2–4.4
// (https://github.com/Zhaoyi-ya/TWS-Pods-PC, 3350f42, GPL-3.0-only; vivo TWS 3e fw 2.5.9 btsnoop).
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>
/// vivo gesture settings as <see cref="KeyFunction"/> slots. Double-tap is per earbud (function =
/// code without the right earbud's 0x10 base); the long-press function applies to both earbuds; the
/// long-press noise cycle is per earbud (button <see cref="CycleButton"/>, function = cycle byte).
/// </summary>
public static class VivoGestures
{
    /// <summary>Button id used for the long-press noise cycle rows.</summary>
    public const int CycleButton = 1;

    /// <summary>Long-press function codes.</summary>
    public const int LongPressNone = 0x01, LongPressNoiseControl = 0x02, LongPressDeclineCall = 0x03;

    /// <summary>Double-tap codes (left earbud; the right earbud adds 0x10).</summary>
    public static IReadOnlyList<int> DoubleTapCodes { get; } = [0x01, 0x02, 0x03, 0x00, 0x05, 0x06];

    /// <summary>Cycle bytes: all three, ANC + transparency, off + ANC, off + transparency. 0xFF (empty) is only reported.</summary>
    public static IReadOnlyList<int> CycleCodes { get; } = [0x0B, 0x0A, 0x08, 0x09];

    public static IReadOnlyList<BrandGestureSlot> Slots { get; } = CreateSlots();

    /// <summary>Recorded function for a reported double-tap byte; codes from the other earbud's range are kept as 0x100 + byte.</summary>
    internal static int DoubleTapFunction(EarSide side, byte code)
    {
        int @base = side == EarSide.Right ? VivoConstants.RightDoubleTapBase : 0;
        return code >= @base && code < @base + 0x10 ? code - @base : 0x100 + code;
    }

    private static IReadOnlyList<BrandGestureSlot> CreateSlots()
    {
        BrandGestureChoice[] doubleTap =
        [
            new(0x01, ["Function_1"]),
            new(0x02, ["Function_5"]),
            new(0x03, ["Function_6"]),
            new(0x00, ["Function_3"]),
            new(0x05, ["Vivo_Function_Translate"]),
            new(0x06, ["Function_0"]),
        ];
        BrandGestureChoice[] longPress =
        [
            new(LongPressNoiseControl, ["Function_8"]),
            new(LongPressDeclineCall, ["Vivo_Function_DeclineCall"]),
            new(LongPressNone, ["Function_0"]),
        ];
        BrandGestureChoice[] cycle =
        [
            new(0x0B, ["NoiseMode_Off", "NoiseMode_NoiseCancellation", "NoiseMode_Transparency"]),
            new(0x0A, ["NoiseMode_NoiseCancellation", "NoiseMode_Transparency"]),
            new(0x08, ["NoiseMode_Off", "NoiseMode_NoiseCancellation"]),
            new(0x09, ["NoiseMode_Off", "NoiseMode_Transparency"]),
        ];
        var noiseHold = new KeyFunction(EarSide.Both, 0, GestureAction.SqueezeAndHold, LongPressNoiseControl);
        return
        [
            new(EarSide.Left, 0, GestureAction.DoubleSqueeze, "Gesture_Tap2", doubleTap),
            new(EarSide.Right, 0, GestureAction.DoubleSqueeze, "Gesture_Tap2", doubleTap),
            new(EarSide.Both, 0, GestureAction.SqueezeAndHold, "Gesture_TapHold", longPress),
            new(EarSide.Left, CycleButton, GestureAction.SqueezeAndHold, "Vivo_HoldCycle", cycle, noiseHold),
            new(EarSide.Right, CycleButton, GestureAction.SqueezeAndHold, "Vivo_HoldCycle", cycle, noiseHold),
        ];
    }
}
