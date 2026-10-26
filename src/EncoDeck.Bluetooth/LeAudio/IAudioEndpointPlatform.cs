// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Result of enabling/disabling one PnP device node. Failures carry the Windows error code.</summary>
public sealed record AudioEndpointOperationResult(bool Success, string? Detail = null)
{
    public static AudioEndpointOperationResult Ok { get; } = new(true);
}

/// <summary>
/// Narrow seam over Windows PnP/MMDEVAPI so endpoint selection and execution conditions can be
/// tested without a Bluetooth adapter. Production uses <see cref="WindowsAudioEndpointPlatform"/>.
/// </summary>
public interface IAudioEndpointPlatform
{
    /// <summary>All MMDEVAPI render endpoints, including disabled ones.</summary>
    Task<IReadOnlyList<AudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken cancellationToken);

    /// <summary>All MMDEVAPI capture endpoints, including disabled ones.</summary>
    Task<IReadOnlyList<AudioEndpointInfo>> GetCaptureEndpointsAsync(CancellationToken cancellationToken);

    /// <summary>Disables or enables exactly one endpoint. No BTHENUM service is touched.</summary>
    Task<AudioEndpointOperationResult> SetEnabledAsync(AudioEndpointInfo endpoint, bool enabled, CancellationToken cancellationToken);

    /// <summary>Makes the endpoint the default console/multimedia render device.</summary>
    Task<AudioEndpointOperationResult> SetDefaultAsync(AudioEndpointInfo endpoint, CancellationToken cancellationToken);
}
