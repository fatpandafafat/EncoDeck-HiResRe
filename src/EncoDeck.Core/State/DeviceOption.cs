// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.State;

/// <summary>
/// A multi-value device setting the earbuds reported themselves (auto power off, a button's
/// assignment, an ambient level). Controllers only publish an option after a valid device readback;
/// the UI renders it generically and every change goes through
/// <see cref="Commands.IAdvancedDeviceCommands.SetOptionAsync"/>.
/// </summary>
/// <param name="Id">Brand-scoped stable id, e.g. <c>sony-auto-power-off</c>.</param>
/// <param name="LabelKey">Localization key of the row title.</param>
/// <param name="Value">Current device value.</param>
/// <param name="Choices">Selectable values; empty for a range option.</param>
/// <param name="Order">Display order among options.</param>
public sealed record DeviceOption(string Id, string LabelKey, int Value, IReadOnlyList<DeviceOptionChoice> Choices, int Order = 0)
{
    /// <summary>Localization key of the row description, if any.</summary>
    public string? DescriptionKey { get; init; }

    /// <summary>Lower bound of a range option (slider).</summary>
    public int Minimum { get; init; }

    /// <summary>Upper bound of a range option (slider).</summary>
    public int Maximum { get; init; }

    /// <summary>A slider between <see cref="Minimum"/> and <see cref="Maximum"/> instead of a list.</summary>
    public bool IsRange => Choices.Count == 0 && Maximum > Minimum;

    /// <summary>Whether <paramref name="value"/> may be written.</summary>
    public bool Accepts(int value) => IsRange ? value >= Minimum && value <= Maximum : Choices.Any(c => c.Value == value);
}

/// <summary>One selectable value. The label is <c>Loc.Format(LabelKey, Argument)</c> when an argument is given.</summary>
public sealed record DeviceOptionChoice(int Value, string LabelKey, string? Argument = null);
