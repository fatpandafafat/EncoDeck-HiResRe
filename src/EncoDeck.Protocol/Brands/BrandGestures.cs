// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Capabilities;
using EncoDeck.Core.State;

namespace EncoDeck.Protocol.Brands;

/// <summary>One selectable value of a gesture slot; the label joins its resource keys with " / ".</summary>
/// <param name="Requires">A capability the device must have confirmed before this value can be chosen; until then it is shown but disabled.</param>
public sealed record BrandGestureChoice(int Function, IReadOnlyList<string> LabelKeys, DeviceCapability? Requires = null);

/// <summary>
/// One device setting shown as a choice list on the controls page, addressed like a
/// <see cref="KeyFunction"/> (<see cref="Side"/>, <see cref="Button"/>, <see cref="Action"/>). The row
/// appears only after the device reported a value for it; a reported value outside
/// <see cref="Choices"/> is shown as unknown and never rewritten.
/// </summary>
/// <param name="ShownWhen">Another slot's value that must currently be reported for this row to apply.</param>
public sealed record BrandGestureSlot(
    EarSide Side,
    int Button,
    GestureAction Action,
    string LabelKey,
    IReadOnlyList<BrandGestureChoice> Choices,
    KeyFunction? ShownWhen = null)
{
    public bool Matches(KeyFunction function) => function.Side == Side && function.Button == Button && function.Action == Action;
}

/// <summary>A profile whose gesture-like settings are described by <see cref="BrandGestureSlot"/> rows.</summary>
public interface IBrandGestureProfile
{
    IReadOnlyList<BrandGestureSlot> GestureSlots { get; }
}
