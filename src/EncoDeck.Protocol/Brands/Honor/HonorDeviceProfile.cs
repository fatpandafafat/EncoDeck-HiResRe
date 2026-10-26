// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Honor;

/// <param name="Choice">
/// HONOR CHOICE (BES) protocol variant: same <c>5A</c> framing, but empty battery / noise queries and a
/// one-byte noise write <c>2B04 [mode]</c> (Nezerkill/honor-earbuds, tested on HONOR CHOICE Earbuds X5;
/// TeskiOke/HonorEarbuds for X5 Pro). Battery and off / noise cancelling / transparency only.
/// </param>
public sealed record HonorModel(string ProductId, string DisplayName, string[] Aliases,
    bool Mbb, bool Noise, bool Wear, bool Dual, bool Eq, bool AncLevels = false, bool Choice = false);

/// <summary>Reviewed HONOR audio plugin 7.0.1.495 and community HONOR CHOICE notes; hardware validation is still required.</summary>
public sealed class HonorDeviceProfile(HonorModel model, IReadOnlyList<byte>? reportedEq = null) : IControllableProfile
{
    private string Evidence => model.Choice
        ? "Community HONOR CHOICE notes (Nezerkill/honor-earbuds, TeskiOke/HonorEarbuds); not verified by EncoDeck."
        : "Official HONOR audio plugin 7.0.1.495; not hardware verified.";
    public HonorModel Model => model;
    public string Brand => "honor";
    public bool IsGeneric => model.ProductId == "generic";
    public string ModelId => "honor-" + model.ProductId.ToLowerInvariant();
    public string DisplayName => model.DisplayName;
    public string ImageKey => ModelId;
    public IReadOnlyList<Guid> ControlServiceUuids => model.Mbb ? [new("00001101-0000-1000-8000-00805f9b34fb")] : [];
    public IReadOnlyCollection<DeviceToggle> Toggles =>
    [
        .. model.Mbb && model.Wear ? new[] { DeviceToggle.InEarDetection } : [],
        .. model.Mbb && model.Dual ? new[] { DeviceToggle.DualDeviceConnection } : [],
    ];
    public IDeviceCapabilities Capabilities => DeviceCapabilities.CreateBuilder()
        .Set(DeviceCapability.BatteryLevel, Support(model.Mbb), Evidence)
        .Set(DeviceCapability.ChargingStatus, Support(model.Mbb), Evidence)
        .Set(DeviceCapability.NoiseCancellation, Support(model.Mbb && model.Noise), Evidence)
        .Set(DeviceCapability.NoiseCancellationLevel, Support(model.Mbb && model.Noise && model.AncLevels), Evidence)
        .Set(DeviceCapability.InEarDetection, Support(model.Mbb && model.Wear), Evidence)
        .Set(DeviceCapability.DualDeviceConnection, Support(model.Mbb && model.Dual), Evidence)
        .Set(DeviceCapability.Equalizer, Support(EqualizerPresets.Count > 0), Evidence)
        .Build();
    private static CapabilitySupport Support(bool supported) => supported ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;
    public IReadOnlyList<NoiseControlOption> NoiseControlOptions => !model.Mbb || !model.Noise ? [] :
    [
        new(NoiseControlMode.Off, null, "Off", "NoiseMode_Off", CapabilitySupport.Supported),
        .. model.AncLevels ? new[] {
            new NoiseControlOption(NoiseControlMode.NoiseCancellation, "cozy", "Comfortable", "Honor_Anc_Cozy", CapabilitySupport.Supported),
            new NoiseControlOption(NoiseControlMode.NoiseCancellation, "general", "Moderate", "Honor_Anc_General", CapabilitySupport.Supported),
            new NoiseControlOption(NoiseControlMode.NoiseCancellation, "ultra", "Deep", "Honor_Anc_Ultra", CapabilitySupport.Supported),
            new NoiseControlOption(NoiseControlMode.NoiseCancellation, "dynamic", "Intelligent", "Honor_Anc_Dynamic", CapabilitySupport.Supported),
        } : new[] { new NoiseControlOption(NoiseControlMode.NoiseCancellation, null, "Noise cancelling", "NoiseMode_NoiseCancellation", CapabilitySupport.Supported) },
        new(NoiseControlMode.Transparency, null, "Transparency", "NoiseMode_Transparency", CapabilitySupport.Supported),
    ];
    internal static readonly (byte Value, string Name, string Key)[] EqNames =
    [
        (0, "Standard", "Honor_Eq_Standard"), (1, "Vocals", "Honor_Eq_Vocals"),
        (2, "Pop", "Honor_Eq_Pop"), (3, "Subwoofer", "Honor_Eq_Subwoofer"),
        (4, "Default", "Honor_Eq_Default"), (5, "Bass boost", "Honor_Eq_Bass"),
        (6, "Treble boost", "Honor_Eq_Treble"), (7, "Classic", "Honor_Eq_Classic"),
        (8, "Deep bass", "Honor_Eq_DeepBass"), (9, "Warm vocals", "Honor_Eq_WarmVocals"),
        (10, "Bright treble", "Honor_Eq_BrightTreble"),
    ];
    public IReadOnlyList<EqualizerPreset> EqualizerPresets => !model.Mbb || !model.Eq || reportedEq is null ? [] :
        [.. EqNames.Where(p => reportedEq.Contains(p.Value)).Select(p => new EqualizerPreset(p.Value.ToString(), p.Name, p.Key, CapabilitySupport.Supported))];
    internal HonorDeviceProfile WithEq(byte[] values) => new(model, values.Distinct().ToArray());
    public int MatchScore(DeviceIdentity identity)
    {
        string name = Normalize(identity.Name);
        if (IsGeneric) return name.StartsWith("honorearbuds", StringComparison.Ordinal) || name.StartsWith("荣耀earbuds", StringComparison.Ordinal) ? 1 : 0;
        // Longest alias across the complete catalog prevents 3i Premium matching ordinary 3i.
        var match = HonorProfiles.Models.SelectMany(m => m.Aliases.Select(a => (Model: m, Alias: Normalize(a))))
            .OrderByDescending(x => x.Alias.Length).FirstOrDefault(x => name == x.Alias || name.StartsWith(x.Alias + "(", StringComparison.Ordinal));
        return match.Model == model ? 100 : 0;
    }
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '(').ToArray());
    public IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context) => new HonorDeviceController(channel, context, this);
}

public static class HonorProfiles
{
    public static IReadOnlyList<HonorModel> Models { get; } =
    [
        new("ZB05", "HONOR Earbuds 2 SE", ["HONOR Earbuds 2 SE", "荣耀Earbuds 2 SE", "HONOR Earbuds 2 Lite", "HONOR Earbuds SE"], true, true, true, false, false),
        new("L001", "HONOR Earbuds 3 Pro", ["HONOR Earbuds 3 Pro", "荣耀Earbuds 3 Pro"], true, true, true, true, false, true),
        new("L008", "HONOR Earbuds X3", ["HONOR Earbuds X3", "荣耀Earbuds X3", "Earbuds X3"], false, false, false, false, false),
        new("L010", "HONOR Earbuds X3i", ["HONOR Earbuds X3i", "荣耀Earbuds X3i", "Earbuds X3i", "Earbuds X3 Lite", "Earbuds X3 SE", "HONOR Earbuds X3 Lite", "HONOR Earbuds X3 SE"], true, false, false, false, true),
        new("L012", "HONOR Earbuds 3i", ["HONOR Earbuds 3i", "荣耀Earbuds 3i"], true, true, true, false, true),
        new("L013", "HONOR Earbuds X5", ["HONOR Earbuds X5", "荣耀Earbuds X5"], true, false, true, true, true),
        new("LE7P", "HONOR Earbuds 3i Premium", ["HONOR Earbuds 3i Premium", "荣耀Earbuds 3i 尊享版"], true, true, true, false, true),
        new("CHOICE-X5", "HONOR CHOICE Earbuds X5", ["HONOR CHOICE Earbuds X5"], true, true, false, false, false, Choice: true),
        new("CHOICE-X5-PRO", "HONOR CHOICE Earbuds X5 Pro", ["HONOR CHOICE Earbuds X5 Pro"], true, true, false, false, false, Choice: true),
    ];
    public static IReadOnlyList<IDeviceProfile> CreateBuiltIn() =>
    [.. Models.Select(m => new HonorDeviceProfile(m)), new HonorDeviceProfile(new("generic", "HONOR earbuds", [], false, false, false, false, false))];
}
