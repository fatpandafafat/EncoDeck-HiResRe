// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Headset capture endpoints (LE Audio and Classic HFP) that belong to the earbuds.</summary>
public sealed record LeAudioMicrophoneInspection(IReadOnlyList<AudioEndpointInfo> Endpoints)
{
    public bool Present => Endpoints.Count > 0;
    public bool Disabled => Present && Endpoints.All(e => e.IsDisabled);
    public bool StateKnown => Endpoints.All(e => e.IsStateKnown);
}

/// <summary>
/// Hides or shows the earbuds' microphone endpoints. An app that opens the headset microphone makes
/// Windows switch the LE Audio CIS to a bidirectional voice configuration (and Classic to HFP),
/// which lowers playback quality for as long as the capture stream stays open. A hidden capture
/// endpoint cannot be opened, so playback keeps the media configuration; the speaker endpoint and
/// the Bluetooth services are not touched. Visibility persists per endpoint until shown again or
/// the earbuds are re-paired (which creates new endpoints).
/// </summary>
public static class LeAudioMicrophone
{
    public static Task<LeAudioMicrophoneInspection> InspectAsync(ulong primary, CancellationToken cancellationToken = default) =>
        InspectAsync(primary, new WindowsAudioEndpointPlatform(), cancellationToken);

    public static async Task<LeAudioMicrophoneInspection> InspectAsync(
        ulong primary, IAudioEndpointPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var endpoints = await platform.GetCaptureEndpointsAsync(cancellationToken).ConfigureAwait(false);
        return Inspect(primary, endpoints);
    }

    public static LeAudioMicrophoneInspection Inspect(ulong primary, IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return new(endpoints.Where(e => e.InstanceId.Contains("{0.0.1.", StringComparison.OrdinalIgnoreCase)
                && e.Kind is AudioEndpointKind.LeAudioCapture or AudioEndpointKind.ClassicHfpCapture
                && e.BelongsTo(primary))
            .ToArray());
    }

    public static Task<AudioEndpointOperationResult> SetEnabledAsync(ulong primary, bool enabled, CancellationToken cancellationToken = default) =>
        SetEnabledAsync(primary, enabled, new WindowsAudioEndpointPlatform(), cancellationToken);

    public static async Task<AudioEndpointOperationResult> SetEnabledAsync(
        ulong primary, bool enabled, IAudioEndpointPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var inspection = await InspectAsync(primary, platform, cancellationToken).ConfigureAwait(false);
        if (!inspection.Present) return new(false, "no headset microphone endpoint");
        if (!inspection.StateKnown) return new(false, "microphone endpoint state is unavailable");

        foreach (var endpoint in inspection.Endpoints.Where(e => e.IsDisabled == enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = await platform.SetEnabledAsync(endpoint, enabled, cancellationToken).ConfigureAwait(false);
            if (!operation.Success) return operation;
        }

        var after = await InspectAsync(primary, platform, cancellationToken).ConfigureAwait(false);
        bool confirmed = inspection.Endpoints.All(before => after.Endpoints.Any(e =>
            e.InstanceId == before.InstanceId && e.IsStateKnown && e.IsDisabled != enabled));
        return confirmed
            ? AudioEndpointOperationResult.Ok
            : new(false, "Windows did not confirm the microphone endpoint change");
    }
}
