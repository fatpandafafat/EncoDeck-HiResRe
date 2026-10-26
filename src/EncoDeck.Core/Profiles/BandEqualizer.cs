// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Profiles;

/// <summary>
/// Limits of a brand's single device-side user EQ curve (non-OPPO brands). Gains are in steps of
/// 1 / <paramref name="GainScale"/> dB (10 = tenths of a dB); band frequencies come from the device.
/// The curve is one fixed slot: it cannot be added, renamed or deleted, and selecting it switches
/// the device to that curve.
/// </summary>
public sealed record BandEqualizerSpec(int MinGain, int MaxGain, int GainScale = 10);

/// <summary>A profile whose controller reports one fixed user curve through <c>DeviceState.CustomEqualizers</c>.</summary>
public interface IBandEqualizerProfile
{
    /// <summary>Null until the device has returned a complete, valid curve.</summary>
    BandEqualizerSpec? BandEqualizer { get; }
}
