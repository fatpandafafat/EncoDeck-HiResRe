// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;
namespace EncoDeck.Protocol.Brands.Sony;

/// <summary>
/// MDR v2 table 1 NC/ASM parameter layouts (inquired types 0x13–0x19). The ambient-sound mode
/// (normal / focus on voice) sits directly before the ambient level, as in the layouts documented by
/// mos9527/SonyHeadphonesClient (libmdr) and checked against its WF-1000XM5 6.1.0 capture.
/// </summary>
public static class SonyV2Noise
{
    public const int MaximumAmbientLevel = 20;

    public static byte? InquiredType(byte function) => function switch
    { 100 => 19, 101 => 20, 104 => 21, 106 => 22, 107 => 23, 108 => 24, 109 => 25, _ => null };

    private static int LevelIndex(byte type) => type is 21 or 22 ? 7 : 6;

    public static bool TryRead(ReadOnlySpan<byte> p, out NoiseControlMode mode) => TryRead(p, out mode, out _, out _);

    /// <summary>Reads mode, focus-on-voice and the stored ambient level (also kept while not in ambient mode).</summary>
    public static bool TryRead(ReadOnlySpan<byte> p, out NoiseControlMode mode, out bool voice, out int level)
    {
        mode = NoiseControlMode.Off; voice = false; level = 0;
        if (p.Length < 7 || p[2] > 1 || p[3] > 1) return false;
        int length = p[1] switch { 19 or 20 or 23 or 24 => 7, 21 or 22 => 8, 25 => 9, _ => 0 };
        if (p.Length != length) return false;
        int levelIndex = LevelIndex(p[1]);
        if (p[levelIndex - 1] > 1 || p[levelIndex] > MaximumAmbientLevel) return false;
        voice = p[levelIndex - 1] == 1;
        level = p[levelIndex];
        if (p[3] == 0) return true;
        if (p[1] is 19 or 20) mode = p[6] > 0 ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
        else mode = p[4] == 1 ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
        return true;
    }

    public static byte[]? SetPayload(ReadOnlySpan<byte> current, NoiseControlMode target) => SetPayload(current, target, null, null);

    /// <summary>
    /// Builds the SET payload (without the command byte) from the device's own readback, so unrelated
    /// fields (NC dual/single, auto ambient, adaptive sensitivity) are preserved.
    /// </summary>
    public static byte[]? SetPayload(ReadOnlySpan<byte> current, NoiseControlMode target, bool? voice, int? level)
    {
        if (!TryRead(current, out _, out _, out _)) return null;
        if (level is < 1 or > MaximumAmbientLevel) return null;
        byte[] p = current.ToArray(); p[0] = 0x68; p[2] = 1;
        int levelIndex = LevelIndex(p[1]);
        p[3] = target == NoiseControlMode.Off ? (byte)0 : (byte)1;
        if (voice is { } focus) p[levelIndex - 1] = focus ? (byte)1 : (byte)0;
        if (p[1] is 19 or 20)
        {
            p[4] = target == NoiseControlMode.NoiseCancellation ? (p[1] == 20 ? (byte)2 : (byte)1) : (byte)0;
            // These layouts tell ambient from NC by a non-zero level only.
            p[6] = target == NoiseControlMode.Transparency ? (byte)(level ?? (p[6] > 0 ? p[6] : MaximumAmbientLevel)) : (byte)0;
        }
        else
        {
            p[4] = target == NoiseControlMode.Transparency ? (byte)1 : (byte)0;
            // Preserve auto/dual choice and noise-adaptation fields supplied by the device.
            if (level is { } requested) p[levelIndex] = (byte)requested;
            else if (target == NoiseControlMode.Transparency && p[levelIndex] == 0) p[levelIndex] = MaximumAmbientLevel;
        }
        return p[1..];
    }
}

/// <summary>
/// MDR v1 NC/ASM (inquired type 2): <c>[67, 02, effect, ncType, ncValue, asmType, asmId, asmValue]</c>.
/// The write follows the frame mos9527/SonyHeadphonesClient confirmed on a WH-1000XM3 4.5.2: level
/// adjustment for both halves, dual NC value 2 for noise cancelling, ambient level 1–20 otherwise.
/// </summary>
public static class SonyV1Noise
{
    public static bool TryRead(ReadOnlySpan<byte> p, out NoiseControlMode mode, out bool voice, out int level)
    {
        mode = NoiseControlMode.Off; voice = false; level = 0;
        if (p.Length != 8 || p[0] is not (0x67 or 0x69) || p[1] != 2 || p[6] > 1 || p[7] > SonyV2Noise.MaximumAmbientLevel) return false;
        voice = p[6] == 1;
        level = p[7];
        if (p[2] == 0) return true;
        mode = p[7] > 0 ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
        return true;
    }

    /// <param name="ambientLevel">Level to use when switching to ambient sound (1–20).</param>
    public static byte[]? SetPayload(ReadOnlySpan<byte> current, NoiseControlMode target, bool? voice, int ambientLevel)
    {
        if (!TryRead(current, out _, out bool currentVoice, out _) || ambientLevel is < 1 or > SonyV2Noise.MaximumAmbientLevel) return null;
        byte[] p = current[1..].ToArray();
        // OFF, or ADJUSTMENT_COMPLETION while already enabled, else ON.
        p[1] = target == NoiseControlMode.Off ? (byte)0 : current[2] != 0 ? (byte)0x11 : (byte)1;
        p[5] = (byte)((voice ?? currentVoice) ? 1 : 0);
        if (target == NoiseControlMode.Off) return p;
        p[2] = 1; p[4] = 1;
        p[3] = target == NoiseControlMode.NoiseCancellation ? (byte)2 : (byte)0;
        p[6] = target == NoiseControlMode.Transparency ? (byte)ambientLevel : (byte)0;
        return p;
    }
}
