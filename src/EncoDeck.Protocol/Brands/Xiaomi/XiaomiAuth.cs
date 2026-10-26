// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Encrypted block corrected after BudsLink (https://github.com/maniacx/BudsLink, GPL-3.0) and mebuds
// (https://github.com/paradox-ng/mebuds, GPL-3.0-or-later); checked against real-device auth vectors
// published by OpenXia (MIT) and BudsLink PR #110, see the tests and THIRD_PARTY_NOTICES.md.
using System.Security.Cryptography;

namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Challenge/response used by Xiaomi earbuds after the RFCOMM channel opens. Each side sends
/// <c>0x01 || random(16)</c>; the other answers <c>0x01 || GetEncryptedAuthCheckData(random)</c>:
/// the fixed address block <c>11 22 33 33 22 11 …</c> encrypted under a key schedule seeded with
/// the challenge (last byte XOR 6), 17 round keys, 8-round substitution/mix network.
/// </summary>
public static class XiaomiAuth
{
    /// <summary>Request payload version byte.</summary>
    public const byte AuthVersion = 0x01;

    /// <summary>Length of the random challenge and of the device's answer.</summary>
    public const int AuthRandomLength = 16;

    private const int Mask = 0x9999;

    /// <summary>The fixed address <c>11 22 33 33 22 11</c> repeated to 16 bytes: the block that is encrypted.</summary>
    private static readonly byte[] AddressBlock = [0x11, 0x22, 0x33, 0x33, 0x22, 0x11, 0x11, 0x22, 0x33, 0x33, 0x22, 0x11, 0x11, 0x22, 0x33, 0x33];

    private static readonly byte[] SBox =
    [
        100, 172, 40, 90, 201, 179, 55, 197, 10, 16, 183, 163, 186, 177, 151, 70, 61, 5, 220, 102, 110, 246, 154, 248, 13, 88, 149, 103, 198, 170, 171, 236, 160, 104, 155, 150, 212, 235, 191, 67, 73, 54, 233, 106, 137, 216, 195, 138, 148, 99, 153, 188, 123, 190, 193, 34, 187, 92, 113, 213, 31, 146, 87, 93, 143, 68, 65, 29, 81, 230, 64, 23, 251, 253, 25, 50, 52, 184, 97, 42, 202, 35, 111, 218, 57, 247, 162, 1, 127, 214, 49, 231, 222, 128, 4, 221, 44, 89, 130, 175, 168, 224, 15, 205, 161, 18, 62, 48, 209, 28, 208, 58, 51, 114, 46, 79, 144, 2, 19, 6, 117, 206, 135, 194, 239, 178, 173, 125, 56, 21, 225, 82, 159, 122, 108, 47, 39, 196, 226, 129, 169, 207, 141, 192, 215, 223, 255, 96, 118, 20, 140, 94, 85, 9, 228, 8, 199, 66, 32, 252, 210, 80, 145, 217, 76, 98, 158, 232, 185, 166, 249, 26, 0, 33, 11, 250, 53, 156, 78, 75, 105, 72, 203, 14, 200, 164, 91, 234, 132, 7, 180, 24, 244, 174, 107, 219, 167, 204, 63, 139, 74, 12, 60, 37, 229, 84, 77, 69, 131, 237, 17, 240, 176, 83, 147, 242, 116, 38, 181, 157, 109, 124, 243, 45, 241, 86, 36, 126, 71, 27, 134, 189, 112, 142, 30, 59, 115, 22, 3, 182, 172, 40, 90, 201, 179, 55, 197, 10, 16, 183, 163, 186, 177, 151, 70, 136
    ];

    private static readonly byte[] T1 =
    [
        1, 45, 226, 147, 190, 69, 21, 174, 120, 3, 135, 164, 184, 56, 207, 63, 8, 103, 9, 148, 235, 38, 168, 107, 189, 24, 52, 27, 187, 191, 114, 247, 64, 53, 72, 156, 81, 47, 59, 85, 227, 192, 159, 216, 211, 243, 141, 177, 255, 167, 62, 220, 134, 119, 215, 166, 17, 251, 244, 186, 146, 145, 100, 131, 241, 51, 239, 218, 44, 181, 178, 43, 136, 209, 153, 203, 140, 132, 29, 20, 129, 151, 113, 202, 95, 163, 139, 87, 60, 130, 196, 82, 92, 28, 232, 160, 4, 180, 133, 74, 246, 19, 84, 182, 223, 12, 26, 142, 222, 224, 57, 252, 32, 155, 36, 78, 169, 152, 158, 171, 242, 96, 208, 108, 234, 250, 199, 217, 0, 212, 31, 110, 67, 188, 236, 83, 137, 254, 122, 93, 73, 201, 50, 194, 249, 154, 248, 109, 22, 219, 89, 150, 68, 233, 205, 230, 70, 66, 143, 10, 193, 204, 185, 101, 176, 210, 198, 172, 30, 65, 98, 41, 46, 14, 116, 80, 2, 90, 195, 37, 123, 138, 42, 91, 240, 6, 13, 71, 111, 112, 157, 126, 16, 206, 18, 39, 213, 76, 79, 214, 121, 48, 104, 54, 117, 125, 228, 237, 128, 106, 144, 55, 162, 94, 118, 170, 197, 127, 61, 175, 165, 229, 25, 97, 253, 77, 124, 183, 11, 238, 173, 75, 34, 245, 231, 115, 35, 33, 200, 5, 225, 102, 221, 179, 88, 105, 99, 86, 15, 161, 49, 149, 23, 7, 58, 40
    ];

    private static readonly byte[] T2 =
    [
        128, 0, 176, 9, 96, 239, 185, 253, 16, 18, 159, 228, 105, 186, 173, 248, 192, 56, 194, 101, 79, 6, 148, 252, 25, 222, 106, 27, 93, 78, 168, 130, 112, 237, 232, 236, 114, 179, 21, 195, 255, 171, 182, 71, 68, 1, 172, 37, 201, 250, 142, 65, 26, 33, 203, 211, 13, 110, 254, 38, 88, 218, 50, 15, 32, 169, 157, 132, 152, 5, 156, 187, 34, 140, 99, 231, 197, 225, 115, 198, 175, 36, 91, 135, 102, 39, 247, 87, 244, 150, 177, 183, 92, 139, 213, 84, 121, 223, 170, 246, 62, 163, 241, 17, 202, 245, 209, 23, 123, 147, 131, 188, 189, 82, 30, 235, 174, 204, 214, 53, 8, 200, 138, 180, 226, 205, 191, 217, 208, 80, 89, 63, 77, 98, 52, 10, 72, 136, 181, 86, 76, 46, 107, 158, 210, 61, 60, 3, 19, 251, 151, 81, 117, 74, 145, 113, 35, 190, 118, 42, 95, 249, 212, 85, 11, 220, 55, 49, 22, 116, 215, 119, 167, 230, 7, 219, 164, 47, 70, 243, 97, 69, 103, 227, 12, 162, 59, 28, 133, 24, 4, 29, 41, 160, 143, 178, 90, 216, 166, 126, 238, 141, 83, 75, 161, 154, 193, 14, 122, 73, 165, 44, 129, 196, 199, 54, 43, 127, 67, 149, 51, 242, 108, 104, 109, 240, 2, 40, 206, 221, 155, 234, 94, 153, 124, 20, 134, 207, 229, 66, 184, 64, 120, 45, 58, 233, 100, 31, 146, 144, 125, 57, 111, 224, 137, 48
    ];

    /// <summary>Computes the answer the earbuds must give for <paramref name="challenge"/> (16 bytes).</summary>
    public static byte[] GetEncryptedAuthCheckData(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != AuthRandomLength) throw new ArgumentException("The challenge must be 16 bytes.", nameof(challenge));

        var state = (byte[])AddressBlock.Clone();

        var scheduleInput = challenge.ToArray();
        scheduleInput[15] ^= 6;
        return RoundFunction(state, KeySchedule(scheduleInput));
    }

    /// <summary>Request payload: <c>[version] || randomFactor(16)</c>.</summary>
    public static byte[] BuildAuthCheckPayload(ReadOnlySpan<byte> randomFactor, byte version = AuthVersion)
    {
        if (randomFactor.Length != AuthRandomLength) throw new ArgumentException("The random factor must be 16 bytes.", nameof(randomFactor));
        var payload = new byte[1 + AuthRandomLength];
        payload[0] = version;
        randomFactor.CopyTo(payload.AsSpan(1));
        return payload;
    }

    /// <summary>Parses the answer payload <c>[version] || result(16)</c>.</summary>
    public static bool TryParseAuthCheckResponse(ReadOnlySpan<byte> payload, out int versionResponse, out byte[] result)
    {
        versionResponse = 0;
        result = [];
        if (payload.Length < 1 + AuthRandomLength) return false;
        versionResponse = payload[0];
        result = payload.Slice(1, AuthRandomLength).ToArray();
        return true;
    }

    /// <summary>A fresh random challenge.</summary>
    public static byte[] CreateRandomFactor() => RandomNumberGenerator.GetBytes(AuthRandomLength);

    /// <summary>True when <paramref name="deviceResult"/> is the expected answer to <paramref name="randomFactor"/>.</summary>
    public static bool VerifyAuthResponse(ReadOnlySpan<byte> randomFactor, ReadOnlySpan<byte> deviceResult) =>
        deviceResult.Length == AuthRandomLength
        && CryptographicOperations.FixedTimeEquals(GetEncryptedAuthCheckData(randomFactor), deviceResult);

    private static byte Rol8(byte x, int n)
    {
        n &= 7;
        return (byte)(((x << n) | (x >> (8 - n))) & 0xff);
    }

    /// <summary>Per-byte ADD/XOR selector: whether bit <paramref name="j"/> of 0x9999 is set.</summary>
    private static bool Sel(int j) => ((1 << j) & Mask) != 0;

    private static byte[][] KeySchedule(byte[] input)
    {
        var rk = new byte[17][];
        for (int i = 0; i < 17; i++) rk[i] = new byte[16];
        Array.Copy(input, rk[0], 16);
        var cur = (byte[])input.Clone();
        var buf = new byte[17];
        for (int r = 0; r < 16; r++)
        {
            int parity = 0;
            foreach (byte b in cur) parity ^= b;
            for (int i = 0; i < 16; i++) buf[i] = Rol8(cur[i], 3);
            buf[16] = Rol8((byte)parity, 3);
            for (int j = 0; j < 16; j++)
            {
                rk[r + 1][j] = (byte)((SBox[15 + (16 * r) - j] + buf[(r + 1 + j) % 17]) & 0xff);
            }
            for (int i = 0; i < 16; i++) cur[i] = Rol8(cur[i], 3);
        }
        return rk;
    }

    // Linear mixing layer, transcribed register by register from the reference.
    private static byte[] Mix(byte[] s)
    {
        int s0=s[0],s1=s[1],s2=s[2],s3=s[3],s4=s[4],s5=s[5],s6=s[6],s7=s[7];
        int s8=s[8],s9=s[9],s10=s[10],s11=s[11],s12=s[12],s13=s[13],s14=s[14],s15=s[15];
        int w16=s0, w17=s1, w3=s2, w4=s3, w5=s4;
        int w7=(w17+2*w16)&0xff;
        int w6=s5;
        w16=(w17+w16)&0xff;
        int w19=s6;
        int w20=(w4+2*w3)&0xff;
        w17=s7;
        w3=(w4+w3)&0xff;
        int w21=s8;
        int w22=(w6+2*w5)&0xff;
        w4=s9;
        w5=(w6+w5)&0xff;
        int w23=s10;
        int w24=(w17+2*w19)&0xff;
        w6=s11;
        w17=(w17+w19)&0xff;
        int w25=s12;
        int w26=(w4+2*w21)&0xff;
        w19=s13;
        w4=(w4+w21)&0xff;
        int w27=s14;
        int w28=(w6+2*w23)&0xff;
        w21=s15;
        w6=(w6+w23)&0xff;
        w23=(w19+2*w25)&0xff;
        w19=(w19+w25)&0xff;
        w25=(w21+2*w27)&0xff;
        w21=(w21+w27)&0xff;
        w27=(w6+2*w26)&0xff;
        w6=(w6+w26)&0xff;
        w26=(w21+2*w23)&0xff;
        w21=(w21+w23)&0xff;
        w23=(w16+2*w20)&0xff;
        w16=(w20+w16)&0xff;
        w20=(w5+2*w24)&0xff;
        w5=(w24+w5)&0xff;
        w24=(w4+2*w28)&0xff;
        w4=(w28+w4)&0xff;
        w28=(w19+2*w25)&0xff;
        w19=(w25+w19)&0xff;
        w25=(w17+2*w7)&0xff;
        w17=(w17+w7)&0xff;
        w7=(w3+2*w22)&0xff;
        w3=(w22+w3)&0xff;
        w22=(w19+2*w24)&0xff;
        w19=(w19+w24)&0xff;
        w24=(w3+2*w25)&0xff;
        w3=(w25+w3)&0xff;
        w25=(w16+2*w20)&0xff;
        w16=(w20+w16)&0xff;
        w20=(w4+2*w28)&0xff;
        w4=(w28+w4)&0xff;
        w28=(w17+2*w7)&0xff;
        w17=(w17+w7)&0xff;
        int w30=(w20+w17)&0xff;
        w17=(w17+2*w20)&0xff;
        w7=(w5+2*w27)&0xff;
        w5=(w27+w5)&0xff;
        w27=(w21+2*w23)&0xff;
        w21=(w21+w23)&0xff;
        int sp0=w17;
        w17=(w19+w24)&0xff;
        w23=(w26+w6)&0xff;
        w19=(w19+2*w24)&0xff;
        int w20b=(w21+w7)&0xff;
        w7=(w21+2*w7)&0xff;
        int sp5=w17;
        w17=(w23+2*w25)&0xff;
        int sp4=w19;
        w19=(w4+w28)&0xff;
        w4=(w4+2*w28)&0xff;
        int sp2=w7;
        int sp6=w17;
        w17=(w27+w5)&0xff;
        w7=(w23+w25)&0xff;
        w5=(w5+2*w27)&0xff;
        int sp8=w4;
        w4=(w22+w16)&0xff;
        w16=(w16+2*w22)&0xff;
        int sp11=w17;
        w17=(w6+2*w26)&0xff;
        int sp1=w30;
        int sp13=w4;
        w4=(w17+w3)&0xff;
        int sp12=w16;
        w16=(w3+2*w17)&0xff;
        int sp3=w20b;
        int sp7=w7;
        int sp9=w19;
        int sp10=w5;
        int sp15=w4;
        int sp14=w16;
        return new byte[]{ (byte)sp0,(byte)sp1,(byte)sp2,(byte)sp3,(byte)sp4,(byte)sp5,(byte)sp6,(byte)sp7,(byte)sp8,(byte)sp9,(byte)sp10,(byte)sp11,(byte)sp12,(byte)sp13,(byte)sp14,(byte)sp15 };
    }

    private static byte[] Substitute(byte[] x)
    {
        var o = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            bool t1 = i is 0 or 3 or 4 or 7 or 8 or 11 or 12 or 15;
            o[i] = t1 ? T1[x[i]] : T2[x[i]];
        }
        return o;
    }

    /// <summary>Key mix: selected bytes XOR and the others ADD; <paramref name="inverted"/> swaps the two.</summary>
    private static byte[] KeyMix(byte[] x, byte[] key, bool inverted)
    {
        var o = (byte[])x.Clone();
        for (int j = 0; j < 16; j++)
        {
            o[j] = Sel(j) != inverted ? (byte)(o[j] ^ key[j]) : (byte)((o[j] + key[j]) & 0xff);
        }
        return o;
    }

    private static byte[] RoundFunction(byte[] state, byte[][] rk)
    {
        var x = (byte[])state.Clone();
        var backup = (byte[])x.Clone();
        for (int r = 0; r < 8; r++)
        {
            if (r == 2) x = KeyMix(x, backup, false);
            x = KeyMix(x, rk[2 * r], false);
            x = Substitute(x);
            x = KeyMix(x, rk[(2 * r) + 1], true);
            x = Mix(x);
        }
        return KeyMix(x, rk[16], false);
    }
}
