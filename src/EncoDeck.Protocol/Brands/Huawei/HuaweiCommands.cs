// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
// Command ids cross-checked with OpenFreebuds (https://github.com/melianmiko/OpenFreebuds, MelianMiko and contributors)
// and HuaweiPods (https://github.com/Nshpiter/HuaweiPods, Nshpiter and contributors), both GPL-3.0.
namespace EncoDeck.Protocol.Brands.Huawei;

/// <summary>HUAWEI command numbers (<c>service &lt;&lt; 8 | command</c>) and well-known TLV values.</summary>
public static class HuaweiCommands
{
    /// <summary>HUAWEI earbuds expose their control protocol on the standard Serial Port Profile service.</summary>
    public static readonly Guid SppServiceUuid = new("00001101-0000-1000-8000-00805F9B34FB");

    /// <summary>Battery query and report: TLV 1 = overall level, 2 = [left, right, case], 3 = charging flags.</summary>
    public const ushort Battery = 0x0108;

    /// <summary>Unsolicited battery report sent by some models.</summary>
    public const ushort BatteryAlt = 0x0127;

    /// <summary>
    /// Device-initiated "big data capability" request (TLV 1). FreeBuds 5i drops the link unless the
    /// host answers TLV 2 = 0 (OpenFreebuds issue #142, real device).
    /// </summary>
    public const ushort BigDataCapability = 0x0A0E;

    /// <summary>Legacy noise-control change push (TLV 1 = new mode) sent after an on-device switch.</summary>
    public const ushort AncChanged = 0x2B03;

    /// <summary>Noise-control state query and report: TLV 1 = [sub-mode, mode].</summary>
    public const ushort QueryAnc = 0x2B2A;

    /// <summary>Noise-control write: TLV 1 = [mode, sub-mode]; the acknowledgement may be missing, so it is confirmed by <see cref="QueryAnc"/>.</summary>
    public const ushort SetAnc = 0x2B04;

    /// <summary>Wear (in-ear) detection write: TLV 1 = 0/1.</summary>
    public const ushort SetWearDetection = 0x2B10;

    /// <summary>Wear detection query/report: TLV 1 = 0/1.</summary>
    public const ushort QueryWearDetection = 0x2B11;

    /// <summary>Per-earbud wear state, query and push: TLV 1 = left, 2 = right (1 = in ear).</summary>
    public const ushort WearState = 0x2B25;

    /// <summary>EQ write: TLV 1 = preset id.</summary>
    public const ushort SetEqualizer = 0x2B49;

    /// <summary>EQ query/report: TLV 2 = selected preset id, 3 = built-in preset ids the earbuds offer.</summary>
    public const ushort QueryEqualizer = 0x2B4A;

    /// <summary>Low-latency (game) mode, read and write: write TLV 1 = 0/1, read answer TLV 2 = 0/1.</summary>
    public const ushort LowLatency = 0x2B6C;

    /// <summary>Dual-connection master switch write: TLV 1 = 0/1.</summary>
    public const ushort SetDualConnect = 0x2B2E;

    /// <summary>Dual-connection master switch query: TLV 1 = 0/1.</summary>
    public const ushort QueryDualConnect = 0x2B2F;

    /// <summary>
    /// Known-device list. One query is answered by one frame per device: TLV 2 = count, 3 = index,
    /// 4 = address (reversed), 5 = state (0 = off, 9 = playing), 7 = preferred, 8 = auto-connect, 9 = name.
    /// </summary>
    public const ushort DualConnectDevices = 0x2B31;

    /// <summary>Pushed when the known-device list changes.</summary>
    public const ushort DualConnectChanged = 0x2B36;

    /// <summary>Feature switches addressed by id (TLV 1); TLV 2.. carry the feature's fields.</summary>
    public const ushort FeatureState = 0x2BB4;

    /// <summary>Feature id: adaptive volume (TLV 2 = 0/1).</summary>
    public const byte FeatureAdaptiveVolume = 0x02;

    /// <summary>Feature id: head gestures for calls (TLV 2 = 0/1, 3 = nod action, 4 = shake action).</summary>
    public const byte FeatureHeadGestures = 0x0B;

    /// <summary>Feature id: independent spatial audio (TLV 2 = 0 off / 1 head tracking / 2 fixed).</summary>
    public const byte FeatureSpatialAudio = 0x18;

    /// <summary>Head-gesture actions: no action, answer, decline.</summary>
    public const byte HeadActionNone = 0, HeadActionAnswer = 1, HeadActionDecline = 2;

    /// <summary>TLV type a device uses to return a 4-byte result code for a command.</summary>
    public const byte ErrorTlv = 0x7F;

    /// <summary>Result code 100000 (<c>7F 04 00 01 86 A0</c>): the write was accepted. Any other code is an error.</summary>
    public const uint ResultSuccess = 100000;

    public const byte AncModeOff = 0x00;
    public const byte AncModeNoiseCancellation = 0x01;
    public const byte AncModeTransparency = 0x02;

    /// <summary>Sub-mode sent when a model has no discrete levels, or to keep the current level.</summary>
    public const byte AncSubModeDefault = 0xFF;
}
