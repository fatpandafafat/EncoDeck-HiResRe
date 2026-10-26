// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Core.Commands;

/// <summary>
/// The only entry point for changing device state. Implementations validate the request against
/// the active profile, send it on the control channel, wait for the device's confirmation, and
/// update <see cref="IDeviceStateStore"/> — rolling back any optimistic value on failure.
/// </summary>
public interface IDeviceCommandService
{
    Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken = default);

    Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken = default);

    /// <summary>Overwrites the band values (dB offsets, -6..+6) of the active editable preset.</summary>
    Task<CommandResult> SetEqualizerBandsAsync(IReadOnlyList<int> bands, CancellationToken cancellationToken = default);

    Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Re-reads battery, noise control and other supported state from the device.</summary>
    Task<CommandResult> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>User-initiated reconnect after the control channel became unavailable.</summary>
    Task<CommandResult> ReconnectAsync(CancellationToken cancellationToken = default);
}
