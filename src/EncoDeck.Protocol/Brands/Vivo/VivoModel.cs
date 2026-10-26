// SPDX-License-Identifier: GPL-3.0-only
// Ported from OppoPodsManager (https://github.com/Zhaoyi-ya/OppoPodsManager), GPL-3.0-or-later.
using System.Text;

namespace EncoDeck.Protocol.Brands.Vivo;

/// <summary>Protocol generation of a model: GAIA version and the noise query/set byte layout.</summary>
public enum VivoGaiaProfile
{
    /// <summary>TWS 4 era and the default for unknown models: v4, query <c>[00]</c>, set suffix <c>[03 01]</c>.</summary>
    FamilyV4,

    /// <summary>vivo TWS 3e: v3, empty query, set suffix <c>[03]</c>.</summary>
    Tws3e,

    /// <summary>vivo / iQOO TWS Air3 Pro: v3, empty query, set suffix <c>[04 00]</c>.</summary>
    Air3Pro,
}

/// <summary>One vivo / iQOO model from the official configuration.</summary>
/// <param name="ModelId">EncoDeck model id, e.g. <c>vivo-tws-4</c>.</param>
/// <param name="Name">Official name, which is also the Bluetooth name.</param>
/// <param name="DisplayName">Name shown in EncoDeck.</param>
/// <param name="ProjectName">Project code name (e.g. <c>DPD2321A</c>) when unique to this model; some Bluetooth names carry it.</param>
/// <param name="VendorModelIds">vivo <c>model</c> ids merged into this row.</param>
/// <param name="Gaia">Protocol generation.</param>
/// <param name="Noise">Declares noise reduction.</param>
/// <param name="AudioEffect">Built-in sound effect table version (0 = none).</param>
/// <param name="Find">Declares find-my-earbuds.</param>
/// <param name="LowLatency">Declares low-latency game mode.</param>
/// <param name="Headphones">Over-ear headphones rather than earbuds.</param>
/// <param name="DualConnection">
/// Declares a manageable dual connection list (official feature 17, per TWS-Pods-PC's feature table).
/// The TWS 3e always connects two devices but answers no list query, so it is not marked.
/// </param>
public sealed record VivoModel(
    string ModelId,
    string Name,
    string DisplayName,
    string? ProjectName,
    int[] VendorModelIds,
    VivoGaiaProfile Gaia,
    bool Noise,
    int AudioEffect,
    bool Find,
    bool LowLatency,
    bool Headphones,
    bool DualConnection = false)
{
    public int GaiaVersion => Gaia == VivoGaiaProfile.FamilyV4 ? 4 : 3;

    /// <summary>Payload of the noise query (<see cref="VivoCommands.QueryNoiseMode"/>).</summary>
    public byte[] NoiseQueryPayload => Gaia == VivoGaiaProfile.FamilyV4 ? [0x00] : [];

    /// <summary>Bytes following the mode byte in <see cref="VivoCommands.SetNoiseMode"/>.</summary>
    public byte[] NoiseSetSuffix => Gaia switch
    {
        VivoGaiaProfile.Tws3e => [0x03],
        VivoGaiaProfile.Air3Pro => [0x04, 0x00],
        _ => [0x03, 0x01],
    };

    /// <summary>Built-in sound effect ids offered for <see cref="AudioEffect"/>.</summary>
    public IReadOnlyList<byte> AudioEffectIds => AudioEffect switch
    {
        0 => [],
        2 or 10 or 3 or 11 => [0, 1, 2, 3, 4],
        4 or 12 => [0, 1, 2, 3, 5],
        5 or 13 or 6 or 14 => [0, 1, 2, 3, 4, 5],
        25 => [0, 1, 2, 3, 11],
        27 => [0, 1, 2, 3, 4, 11],
        28 => [0, 1, 2, 3, 5, 11],
        30 => [0, 1, 2, 3, 4, 5, 11],
        _ => [0, 1, 2, 3],
    };

    /// <summary>Normalized name: lower case, letters and digits only.</summary>
    internal static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>Lookup over <see cref="All"/>.</summary>
internal static partial class VivoCatalog
{
    /// <summary>Generic fallback for vivo / iQOO earbuds whose name is not in the catalog.</summary>
    public static VivoModel Generic { get; } = new(
        "vivo-generic", "vivo / iQOO earbuds", "vivo / iQOO earbuds", null, [], VivoGaiaProfile.FamilyV4,
        Noise: false, AudioEffect: 0, Find: false, LowLatency: false, Headphones: false);

    /// <summary>EQ preset id used by EncoDeck for a sound effect id.</summary>
    public static string PresetId(byte effectId) => $"vivo-eq-{effectId}";

    public static bool TryParsePresetId(string presetId, out byte effectId)
    {
        effectId = 0;
        return presetId.StartsWith("vivo-eq-", StringComparison.Ordinal) && byte.TryParse(presetId.AsSpan(8), out effectId);
    }

    /// <summary>
    /// Maps a reported sound effect to the preset shown: game-scene effects 6–10 are shown as
    /// "immersive" (4) like the official app; DeepX custom (0x10) is no preset (null).
    /// </summary>
    public static byte? NormalizeReportedEffect(byte reported) => reported switch
    {
        >= 6 and <= 10 => 4,
        VivoConstants.DeepXCustomEffect => null,
        _ => reported,
    };

    /// <summary>English label and resource key of a sound effect.</summary>
    public static (string English, string Key) EffectName(byte id) => id switch
    {
        0 => ("Standard", "Vivo_Eq_Standard"),
        1 => ("Clear voice", "Vivo_Eq_ClearVoice"),
        2 => ("Mega bass", "Vivo_Eq_MegaBass"),
        3 => ("Crisp highs", "Vivo_Eq_CrispHighs"),
        4 => ("Immersive audio", "Vivo_Eq_Immersive"),
        5 => ("Audiobooks", "Vivo_Eq_Audiobooks"),
        11 => ("Ultimate master", "Vivo_Eq_UltimateMaster"),
        _ => ($"Preset {id}", $"Eq_UnknownPreset:{id}"),
    };
}
