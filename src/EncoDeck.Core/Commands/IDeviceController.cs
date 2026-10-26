// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;

namespace EncoDeck.Core.Commands;

/// <summary>
/// Drives one open control channel for one device, whatever its brand protocol: runs the start-up
/// sequence, turns device reports into <see cref="IDeviceStateStore"/> updates and executes
/// commands with device confirmation. Commands a brand has no implementation for return
/// <see cref="CommandStatus.Unsupported"/>.
/// </summary>
public interface IDeviceController : IAdvancedDeviceCommands, IAsyncDisposable
{
    /// <summary>Runs the brand's session start. Returns false when the device does not answer.</summary>
    Task<bool> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Completes when the underlying control channel closes.</summary>
    Task Closed { get; }

    /// <summary>Raised when the case lid opens while connected (pop-up trigger), where the brand reports it.</summary>
    event EventHandler? CaseLidOpened;

    Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken);

    Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites the band values of the active preset with the given dB offsets (-6..+6), for brands
    /// whose presets expose editable bands (Sony "manual" and custom presets).
    /// </summary>
    Task<CommandResult> SetEqualizerBandsAsync(IReadOnlyList<int> bands, CancellationToken cancellationToken);

    Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken cancellationToken);

    Task<CommandResult> RefreshAsync(CancellationToken cancellationToken);
}
