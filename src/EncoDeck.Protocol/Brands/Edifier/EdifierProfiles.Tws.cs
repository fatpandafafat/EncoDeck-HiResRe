// SPDX-License-Identifier: GPL-3.0-only
// Model facts from Edifier Connect 8.5.3 assets/products_release.json.
// Community TWS models: see CreateCommunityTwsProfiles (HyperEars and BudsLink, GPL-3.0).
using EncoDeck.Core.Profiles;

namespace EncoDeck.Protocol.Brands.Edifier;

public static partial class EdifierProfiles
{
    private static IEnumerable<IDeviceProfile> CreateTwsProfiles() =>
    [
        new EdifierProfile("edifier-170", "EDIFIER Lolli 3", "lolli3", false, isTws: true, hasSpp: true, gattService: "48097001-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-235", "EDIFIER Lolli3 ANC", "lolli3anc", false, isTws: true, hasSpp: true, gattService: "48099b01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-180", "EDIFIER LolliPods 2022", "lollipods2022", false, isTws: true, hasSpp: true, gattService: "48097601-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-203", "EDIFIER Lolli Pro 2", "lollipro2", false, isTws: true, hasSpp: true, gattService: "48099701-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-110", "EDIFIER Lolli Pods Pro", "lollipodspro", false, isTws: true, hasSpp: false, gattService: "48092401-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-401", "EDIFIER LolliClip", "lolliclip", false, isTws: true, hasSpp: true, gattService: "4809ee01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-250", "EDIFIER ATOMBUDS at03", "atombudsat03", false, isTws: true, hasSpp: true, gattService: "4809b301-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-258", "EDIFIER Atombuds AS02", "atombudsas02", false, isTws: true, hasSpp: true, gattService: "4809b201-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-264", "EDIFIER AtomBuds", "atombuds", false, isTws: true, hasSpp: true, gattService: "4809b501-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-276", "EDIFIER ATOMBUDS A1", "atombudsa1", false, isTws: true, hasSpp: true, gattService: "48091b01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-365", "EDIFIER Lolli Pro 3", "lollipro3", false, isTws: true, hasSpp: true, gattService: "4809d601-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-382", "EDIFIER Lolli Pro 2S", "lollipro2s", false, isTws: true, hasSpp: true, gattService: "4809ed01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-387", "EDIFIER Atom Max", "atommax", false, isTws: true, hasSpp: true, gattService: "4809eb01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-400", "EDIFIER AtomBuds Plus", "atombudsplus", false, isTws: true, hasSpp: true, gattService: "48090701-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-467", "EDIFIER Lolli Pro 3i", "lollipro3i", false, isTws: true, hasSpp: true, gattService: "48093401-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-478", "EDIFIER Lolli ANC", "lollianc", false, isTws: true, hasSpp: true, gattService: "48094201-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-479", "EDIFIER Lolli Pro 5", "lollipro5", false, isTws: true, hasSpp: true, gattService: "48093f01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-482", "EDIFIER Atom Lace", "atomlace", false, isTws: true, hasSpp: true, gattService: "48094e01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-485", "EDIFIER Atom Dot", "atomdot", false, isTws: true, hasSpp: true, gattService: "48093c01-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-519", "EDIFIER LolliClip AI", "lolliclipai", false, isTws: true, hasSpp: true, gattService: "48092803-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-522", "EDIFIER Lolli Air", "lolliair", false, isTws: true, hasSpp: true, gattService: "48091403-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-536", "EDIFIER Lolli Pro SE", "lolliprose", false, isTws: true, hasSpp: true, gattService: "48092f03-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-541", "EDIFIER Atom CC", "atomcc", false, isTws: true, hasSpp: true, gattService: "48090603-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-546", "EDIFIER Atom ANC", "atomanc", false, isTws: true, hasSpp: true, gattService: "4809e201-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-547", "EDIFIER Atom Evo", "atomevo", false, isTws: true, hasSpp: true, gattService: "48093503-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-578", "EDIFIER Lolli5 ANC", "lolli5anc", false, isTws: true, hasSpp: true, gattService: "48093203-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-586", "EDIFIER LolliClip SE", "lolliclipse", false, isTws: true, hasSpp: true, gattService: "48096603-1a48-11e9-ab14-d663bd873d93"),
        new EdifierProfile("edifier-605", "EDIFIER Atom Max 2.0", "atommax20", false, isTws: true, hasSpp: true, gattService: "48098903-1a48-11e9-ab14-d663bd873d93"),
        .. CreateCommunityTwsProfiles(),
    ];

    // Models recorded on real devices by community projects: HyperEars docs/edifier-bes-protocol.md
    // and PRs #17 / #33 / #54 / #62 (https://github.com/silverpoetry/HyperEars, 361d279, GPL-3.0-only),
    // and BudsLink deviceConfigs/X5Pro.js + PR #76 (https://github.com/maniacx/BudsLink, ba3160d, GPL-3.0).
    private static IEnumerable<IDeviceProfile> CreateCommunityTwsProfiles() =>
    [
        new EdifierProfile("edifier-evo-pro", "EDIFIER 花再 Evo Pro", "evopro", false, isTws: true, hasSpp: true,
            spec: new EdifierSpec(EdifierFraming.V2Xor, StateBattery: true, EdifierAnc.SlotEvo, EdifierFeatures.Noise,
                Evidence: "花再 Evo Pro: HyperEars community real-device capture (L/R battery, noise slot 0x1B incl. wind)",
                RequireBrand: true)),
        new EdifierProfile("edifier-fitbuds-turbo", "EDIFIER FitBuds Turbo", "fitbudsturbo", false, isTws: true, hasSpp: true,
            spec: new EdifierSpec(EdifierFraming.V2, StateBattery: true, EdifierAnc.SlotEvo, EdifierFeatures.Noise | EdifierFeatures.GameMode,
                Evidence: "FitBuds Turbo: HyperEars PR #62 contributor device (plaintext payloads, slot 0x1B, game mode)")),
        new EdifierProfile("edifier-fitclip-ultra", "EDIFIER FitClip Ultra", "fitclipultra", false, isTws: true, hasSpp: true,
            spec: new EdifierSpec(EdifierFraming.V2Xor, StateBattery: true, EdifierAnc.None, EdifierFeatures.GameMode,
                Evidence: "FitClip Ultra: HyperEars PR #33 / #54 real device (L/R battery, game mode; no noise control)")),
        new EdifierProfile("edifier-x5-pro", "EDIFIER X5 Pro", "x5pro", false, isTws: true, hasSpp: false,
            gattService: "48098001-1a48-11e9-ab14-d663bd873d93", alternateGattService: "48090302-1a48-11e9-ab14-d663bd873d93",
            spec: new EdifierSpec(EdifierFraming.V2Xor, StateBattery: true, EdifierAnc.SlotX5,
                EdifierFeatures.Noise | EdifierFeatures.GameMode | EdifierFeatures.InEarSwitch,
                Experimental: EdifierFeatures.GameMode | EdifierFeatures.InEarSwitch,
                Evidence: "X5 Pro: BudsLink PR #76 contributor test (L/R/case battery and noise group 0x17 samples)",
                RequireBrand: true)),
    ];
}
