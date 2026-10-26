// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Brands.Akg;

/// <summary>
/// AKG BLE control services. Facts only (no code copied): the BES service and its RX/TX roles were
/// confirmed on a real N400NC by tthayer/akg_earbud_control (184d2e2); the Airoha RACE service and
/// characteristics were observed on a real N9 Hybrid by kalicyh/Raito (5804b65).
/// </summary>
public static class AkgBleUuids
{
    /// <summary>BES <c>[AA][cmd][len][payload]</c> control service of N400NC / N200NC / N700NCM2 / Y600.</summary>
    public static readonly Guid BesService = new("65786365-6c70-6f69-6e74-2e636f6d0000");

    /// <summary>Device-to-host notifications.</summary>
    public static readonly Guid BesNotify = new("65786365-6c70-6f69-6e74-2e636f6d0001");

    /// <summary>Host-to-device writes (write with response).</summary>
    public static readonly Guid BesWrite = new("65786365-6c70-6f69-6e74-2e636f6d0002");

    /// <summary>Airoha RACE over GATT (N5 / N9 Hybrid).</summary>
    public static readonly Guid RaceService = new("5052494d-2dab-0341-6972-6f6861424c45");

    public static readonly Guid RaceWrite = new("43484152-2dab-3241-6972-6f6861424c45");

    public static readonly Guid RaceNotify = new("43484152-2dab-3141-6972-6f6861424c45");

    /// <summary>Airoha RACE over classic RFCOMM.</summary>
    public static readonly Guid RaceSpp = new("00000000-0000-0000-0099-aabbccddeeff");

    public static readonly Guid Spp = new("00001101-0000-1000-8000-00805f9b34fb");
}
