// SPDX-License-Identifier: GPL-3.0-only
// Barracuda X settings ported from nightdevx/barracuda-x-battery barracuda.py (MIT, Copyright (c) 2026
// nightdijital). BlackShark V2 Pro 2.4 settings ported from the OpenRazer razerblackshark driver proposal
// (openrazer/openrazer#2862) and AbdullahSAhmad/blackshark-control, both GPL-2.0-or-later.
// See THIRD_PARTY_NOTICES.md.
namespace EncoDeck.Protocol.Brands.Usb;

/// <summary>Airoha customer-data registers. The GET id is listed; the SET id is GET | 0x80.</summary>
public enum RazerPaSetting : byte
{
    EqPreset = 0x13,
    /// <summary>Ten bands, 31 Hz to 16 kHz.</summary>
    CustomEq = 0x15,
    /// <summary>Microphone monitoring on/off.</summary>
    Sidetone = 0x18,
    SidetoneLevel = 0x19,
    /// <summary>Bluetooth do-not-disturb while on 2.4 GHz.</summary>
    DoNotDisturb = 0x27,
    /// <summary>Minutes idle before power-off; 0 = never.</summary>
    AutoOff = 0x2C,
}

/// <summary>
/// Settings whose SET and read-back were confirmed on real receivers: Barracuda X 1532:0550 (every value
/// below, nightdevx/barracuda-x-battery) and BlackShark V2 Pro 2.4 1532:0555 (OpenRazer #2862 testers,
/// firmware 2.4 to 2.6). The 0552 receiver and other PIDs get no writes. A write only counts once a
/// GET returns exactly the written bytes.
/// </summary>
public static class RazerPaSettings
{
    public const ushort BarracudaX = 0x0550, BlackSharkV2Pro = 0x0555;

    public static bool Supports(ushort product, RazerPaSetting setting) => (product, setting) switch
    {
        (BarracudaX, RazerPaSetting.EqPreset or RazerPaSetting.CustomEq or RazerPaSetting.DoNotDisturb or RazerPaSetting.AutoOff) => true,
        // BlackShark custom bands only latch after repeated apply sequences and their range differs between
        // sources, so only the preset selector is written there.
        (BlackSharkV2Pro, RazerPaSetting.EqPreset or RazerPaSetting.Sidetone or RazerPaSetting.SidetoneLevel or
            RazerPaSetting.DoNotDisturb or RazerPaSetting.AutoOff) => true,
        _ => false,
    };

    public static bool IsValid(ushort product, RazerPaSetting setting, ReadOnlySpan<byte> values)
    {
        if (!Supports(product, setting)) return false;
        if (setting == RazerPaSetting.CustomEq)
        {
            // 0..10 with 5 = flat.
            foreach (byte band in values) if (band > 10) return false;
            return values.Length == 10;
        }
        if (values.Length != 1) return false;
        byte value = values[0];
        return (product, setting) switch
        {
            (_, RazerPaSetting.Sidetone or RazerPaSetting.DoNotDisturb) => value <= 1,
            (BarracudaX, RazerPaSetting.EqPreset) => value is 0x00 or 0x07 or 0x08 or 0x09 or 0xFF,
            (BarracudaX, RazerPaSetting.AutoOff) => value is 0 or 5 or 15 or 30 or 45 or 60,
            // Game 07, music 08, movie 09, custom FF and the five game-specific curves FA..FE.
            (BlackSharkV2Pro, RazerPaSetting.EqPreset) => value is 0x07 or 0x08 or 0x09 or 0xFF or (>= 0xFA and <= 0xFE),
            (BlackSharkV2Pro, RazerPaSetting.AutoOff) => value is 0 or (>= 15 and <= 60),
            (BlackSharkV2Pro, RazerPaSetting.SidetoneLevel) => value is >= 1 and <= 10,
            _ => false,
        };
    }

    /// <summary>The preset family flag sent with the BlackShark selector: 02 for the game-specific curves.</summary>
    public static byte PresetFamily(byte preset) => preset is >= 0xFA and <= 0xFE ? (byte)2 : (byte)1;

    public static byte[] Get(ushort product, byte sequence, RazerPaSetting setting)
    {
        if (!Supports(product, setting)) throw new ArgumentOutOfRangeException(nameof(setting));
        return Wrap(product, sequence, 0x08, [(byte)setting, 0, 0]);
    }

    public static byte[] Set(ushort product, byte sequence, RazerPaSetting setting, ReadOnlySpan<byte> values)
    {
        if (!IsValid(product, setting, values)) throw new ArgumentOutOfRangeException(nameof(values));
        return Wrap(product, sequence, 0x08, [(byte)((byte)setting | 0x80), 0, (byte)values.Length, .. values]);
    }

    /// <summary>BlackShark selector sequence: GET 1E before the preset, SET 9D (family flag) after it.</summary>
    public static byte[] PrepareEq(byte sequence) => Wrap(BlackSharkV2Pro, sequence, 0x08, [0x1E, 0, 0]);
    public static byte[] EqFamily(byte sequence, byte preset) => Wrap(BlackSharkV2Pro, sequence, 0x08, [0x9D, 0, 1, PresetFamily(preset)]);

    public static byte[] Remote(ushort product, byte sequence, bool enabled) => Wrap(product, sequence, 0x0E, [0xE1, enabled ? (byte)1 : (byte)0]);

    // 0550: YS-Tech envelope with CRC and a running sequence. 0555: the same layout with the CRC field and
    // sequence left zero, exactly as the BlackShark sources send it.
    private static byte[] Wrap(ushort product, byte sequence, byte group, ReadOnlySpan<byte> body)
    {
        if (product == BarracudaX && sequence is 0 or > 127) throw new ArgumentOutOfRangeException(nameof(sequence));
        byte[] frame = [0x50, 0x41, group, product == BarracudaX ? sequence : (byte)0, (byte)body.Length, .. body];
        if (product == BarracudaX) return BarracudaXProtocol.YsTech(frame);
        if (product != BlackSharkV2Pro) throw new ArgumentOutOfRangeException(nameof(product));
        var report = new byte[64];
        report[0] = 2; report[1] = 0x80; report[2] = (byte)frame.Length;
        frame.CopyTo(report, 5);
        return report;
    }

    /// <summary>Values of a GET reply (PI frame, op 01). Unsolicited reports (op 02) are not replies.</summary>
    public static byte[]? Values(ReadOnlySpan<byte> frame, RazerPaSetting setting)
    {
        if (frame.Length < 13 || frame[0] != 0x50 || frame[1] != 0x49 || frame[2] != 0x08 ||
            frame[10] != (byte)setting || frame[11] != 1 || frame.Length < 13 + frame[12]) return null;
        return frame.Slice(13, frame[12]).ToArray();
    }

    /// <summary>Acknowledgement of a SET. It does not prove the value was applied; only a read-back does.</summary>
    public static bool IsSetReply(ReadOnlySpan<byte> frame, RazerPaSetting setting) =>
        frame.Length >= 12 && frame[0] == 0x50 && frame[1] == 0x49 && frame[2] == 0x08 && frame[10] == ((byte)setting | 0x80);
}
