// SPDX-License-Identifier: GPL-3.0-only
// VID/PID and model facts ported from BudsLink (https://github.com/maniacx/BudsLink, GPL-3.0,
// revision ba3160dfce72c192ebbdf2697b0341aa1e78fb56), mebuds (https://github.com/paradox-ng/mebuds,
// GPL-3.0-or-later) and redmi-buds-bar by robin-liquidium
// (https://github.com/robin-liquidium/redmi-buds-bar, GPL-3.0); the Redmi Buds 6 Play id matches
// OpenXia's capture (MIT, see THIRD_PARTY_NOTICES.md).
namespace EncoDeck.Protocol.Brands.Xiaomi;

/// <summary>
/// Models known from community projects, keyed by the VID/PID the earbuds report in device info.
/// Used to find the catalog entry when the Bluetooth name does not match it, and to record what
/// real-device reports say beyond the catalog. It never enables a feature on its own: features
/// still open only after the earbuds answer for them.
/// </summary>
/// <param name="Name">Model name as the earbuds / catalog spell it.</param>
/// <param name="VendorId">USB-style vendor id from device-info record 3.</param>
/// <param name="ProductIds">Product ids seen for the model (regional variants).</param>
/// <param name="Anc">
/// True for ANC models that community model configs list with noise control while the catalog
/// has no level list for them; false when real-device reports show the firmware answers the noise
/// setting without having ANC (Redmi Buds 6 Play, REDMI Buds 8 Active); null when the catalog decides.
/// </param>
/// <param name="FindCommand">False where a real-device report found no find-earbuds command (Redmi Buds 6 Lite).</param>
internal sealed record XiaomiCommunityModel(string Name, int VendorId, int[] ProductIds, bool? Anc = null, bool FindCommand = true)
{
    private const int Xiaomi = 0x2717;

    internal static XiaomiCommunityModel? Find(int vendorId, int productId) =>
        All.FirstOrDefault(m => m.VendorId == vendorId && m.ProductIds.Contains(productId));

    internal static XiaomiCommunityModel? FindByName(string? name) =>
        All.FirstOrDefault(m => Normalize(m.Name) == Normalize(name));

    /// <summary>Same comparison as the catalog lookup: letters and digits only, case-insensitive.</summary>
    private static string Normalize(string? name) => new((name ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Left out: product ids shared by two BudsLink entries (0x5066), placeholder ids, entries whose
    // catalog name is uncertain (Open Wear, Air SE). RedmiBuds8Pro.js carries the name "Redmi Buds 6
    // Pro"; its ids belong to the REDMI Buds 8 Pro (0x50E3 confirmed by robin-liquidium's device).
    private static readonly XiaomiCommunityModel[] All =
    [
        new("Redmi Buds 6 Play", Xiaomi, [0x509B], Anc: false),
        new("REDMI Buds 8 Active", Xiaomi, [0x50E1, 0x50E2], Anc: false),
        new("REDMI Buds 8 Lite", Xiaomi, [0x50EE, 0x50EF]),
        new("REDMI Buds 8 Pro", Xiaomi, [0x50E3, 0x50E5]),
        new("REDMI Buds 8", Xiaomi, [0x50F2]),
        new("REDMI Buds 7S", Xiaomi, [0x50B9]),
        new("REDMI Buds 6 Pro", Xiaomi, [0x509D, 0x509E, 0x50AF]),
        new("Redmi Buds 6", Xiaomi, [0x509F, 0x50A0]),
        new("Redmi Buds 6S", Xiaomi, [0x5095]),
        new("Redmi Buds 6 Lite", Xiaomi, [0x508A, 0x508B], FindCommand: false),
        new("Redmi Buds 6 Active", Xiaomi, [0x5088, 0x5089]),
        new("REDMI Buds SE", Xiaomi, [0x509A]),
        new("Redmi Buds 5 Pro", Xiaomi, [0x506C, 0x506D, 0x506F]),
        new("Redmi Buds 5", Xiaomi, [0x506A, 0x506B, 0x5075]),
        new("Redmi Buds 4", Xiaomi, [0x5037, 0x505D]),
        new("Redmi Buds 4 Active", Xiaomi, [0x5069, 0x505F]),
        new("Redmi Buds 4 Pro", 0x5A4D, [0xEA0E, 0xEA0F]),
        new("Redmi Buds 3", Xiaomi, [0x5027, 0x502A]),
        new("Redmi Buds 3 Pro", 0x5A4D, [0xEA0A, 0xEA0B], Anc: true),
        new("Redmi AirDots 3 Pro", 0x5A4D, [0xEA03, 0xEA0D], Anc: true),
        new("Xiaomi Buds 3", Xiaomi, [0x5026, 0x502B, 0x505E, 0x505A]),
        new("Xiaomi Buds 3 Pro", Xiaomi, [0x5025]),
        new("Xiaomi Buds 3T Pro", Xiaomi, [0x502D]),
        new("Xiaomi Buds 4", Xiaomi, [0x5044], Anc: true),
        new("Xiaomi Buds 4 Pro", Xiaomi, [0x5035, 0x503B]),
        new("Xiaomi Buds 5", Xiaomi, [0x5081, 0x5082]),
        new("Xiaomi Buds 5 Pro", Xiaomi, [0x50AD, 0x50B4, 0x50AB, 0x50AC]),
        new("Xiaomi Buds 6", Xiaomi, [0x50EA]),
        new("FlipBuds Pro", 0x0001, [], Anc: true),
    ];
}
