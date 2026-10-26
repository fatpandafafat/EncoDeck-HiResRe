// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Which render endpoints Windows currently exposes for the earbuds.</summary>
public enum LeAudioAudioEndpointState
{
    Unknown,
    /// <summary>Only the LE Audio render endpoint is present.</summary>
    LeAudioOnly,
    /// <summary>LE Audio and an enabled Classic A2DP render endpoint are both present.</summary>
    ClassicA2dpEnabled,
    /// <summary>The Classic A2DP render endpoint exists but is disabled.</summary>
    ClassicA2dpDisabled,
    /// <summary>A Classic A2DP endpoint is present but no matching LE Audio render endpoint was found.</summary>
    LeAudioEndpointMissing,
    /// <summary>More than one endpoint matches; nothing is changed.</summary>
    Ambiguous,
}

/// <summary>Read-only endpoint evidence for the earbuds. HFP is reported but never disabled automatically.</summary>
public sealed record LeAudioEndpointInspection(
    LeAudioAudioEndpointState State,
    AudioEndpointInfo? LeAudioRender = null,
    AudioEndpointInfo? ClassicA2dpRender = null,
    AudioEndpointInfo? ClassicHfpRender = null,
    string? Detail = null)
{
    public bool LeAudioRenderPresent => LeAudioRender is { IsDisabled: false, IsActive: true, IsStateKnown: true };
    public bool ClassicA2dpPresent => ClassicA2dpRender is not null;
    public bool ClassicA2dpDisabled => ClassicA2dpRender?.IsDisabled == true;

    /// <summary>An enabled Classic render endpoint can steal playback from the LE Audio CIS.</summary>
    public bool HasResidualClassicAudio =>
        ClassicA2dpRender is { IsDisabled: false } || ClassicHfpRender is { IsDisabled: false };
}

public enum LeAudioEndpointOutcome
{
    Disabled,
    AlreadyLeAudioOnly,
    NoClassicEndpoint,
    Enabled,
    NothingToRestore,
    BlockedLeLinks,
    BlockedLeAudioEndpointMissing,
    Ambiguous,
    Failed,
}

public sealed record LeAudioEndpointResult(
    LeAudioEndpointOutcome Outcome,
    string? Detail = null,
    LeAudioEndpointInspection? Inspection = null);

/// <summary>
/// Detects the Classic A2DP render endpoint that Windows derives through CTKD next to the LE Audio
/// endpoint, switches the default output to LE Audio, then hides only that MMDEVAPI render node.
/// The BTHENUM service/parent is never touched: doing so also tears down the LE Audio endpoint when
/// an LE link is down.
///
/// Execution is deliberately conservative: the caller must have observed both LE links connected and
/// a matching LE Audio render endpoint before anything is disabled. Every operation re-inspects and
/// reports the actual result instead of assuming success.
/// </summary>
public static class LeAudioClassicAudioEndpoint
{
    public static Task<LeAudioEndpointInspection> InspectAsync(ulong primary, CancellationToken cancellationToken = default) =>
        InspectAsync(primary, new WindowsAudioEndpointPlatform(), cancellationToken);

    public static async Task<LeAudioEndpointInspection> InspectAsync(
        ulong primary, IAudioEndpointPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var endpoints = await platform.GetRenderEndpointsAsync(cancellationToken).ConfigureAwait(false);
        return Inspect(primary, endpoints);
    }

    /// <summary>Pure classification used by tests and by the platform-backed overload.</summary>
    public static LeAudioEndpointInspection Inspect(ulong primary, IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Pure callers may pass capture nodes too; only {0.0.0...} is a render endpoint.
        var render = endpoints.Where(e => e.InstanceId.Contains("{0.0.0.", StringComparison.OrdinalIgnoreCase)).ToList();
        var a2dpCandidates = render.Where(e => e.Kind == AudioEndpointKind.ClassicA2dpRender && e.ParentContainsAddress(primary)).ToList();
        var hfpCandidates = render.Where(e => e.Kind == AudioEndpointKind.ClassicHfpRender && e.ParentContainsAddress(primary)).ToList();
        if (a2dpCandidates.Count > 1 || hfpCandidates.Count > 1)
            return new(LeAudioAudioEndpointState.Ambiguous, Detail: "more than one Classic render endpoint matches");
        AudioEndpointInfo? a2dp = a2dpCandidates.SingleOrDefault();
        AudioEndpointInfo? hfp = hfpCandidates.SingleOrDefault();
        // A name, or being the sole LE output, is not device identity. Renamed/identical headsets
        // must not redirect defaults to another device before disabling this one's working audio.
        var leCandidates = render.Where(e => e.Kind == AudioEndpointKind.LeAudioRender && e.BelongsTo(primary)).ToList();
        var activeLe = leCandidates.Where(e => e is { IsStateKnown: true, IsActive: true, IsDisabled: false }).ToList();
        if (activeLe.Count > 0) leCandidates = activeLe;

        AudioEndpointInfo? le = leCandidates.Count == 1 ? leCandidates[0] : null;
        if (leCandidates.Count > 1)
            return new(LeAudioAudioEndpointState.Ambiguous, null, a2dp, hfp, "more than one LE Audio render endpoint matches");
        if (le is { IsStateKnown: false } || a2dp is { IsStateKnown: false } || hfp is { IsStateKnown: false })
            return new(LeAudioAudioEndpointState.Unknown, le, a2dp, hfp, "audio endpoint state is unavailable");
        if (le is { IsDisabled: true } or { IsActive: false })
            return new(LeAudioAudioEndpointState.LeAudioEndpointMissing, le, a2dp, hfp, "LE Audio render endpoint is not active");

        if (a2dp is { IsDisabled: true })
            return new(LeAudioAudioEndpointState.ClassicA2dpDisabled, le, a2dp, hfp);

        if (a2dp is { IsDisabled: false })
        {
            return le is null
                ? new(LeAudioAudioEndpointState.LeAudioEndpointMissing, null, a2dp, hfp, "LE Audio render endpoint not found")
                : new(LeAudioAudioEndpointState.ClassicA2dpEnabled, le, a2dp, hfp);
        }

        if (le is not null)
            return new(LeAudioAudioEndpointState.LeAudioOnly, le, null, hfp);

        return new(LeAudioAudioEndpointState.Unknown, null, null, hfp, "no matching render endpoint");
    }

    public static Task<LeAudioEndpointResult> SuppressAsync(
        ulong primary, bool bothLeConnected, CancellationToken cancellationToken = default) =>
        SuppressAsync(primary, bothLeConnected, new WindowsAudioEndpointPlatform(), cancellationToken);

    public static async Task<LeAudioEndpointResult> SuppressAsync(
        ulong primary, bool bothLeConnected, IAudioEndpointPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        cancellationToken.ThrowIfCancellationRequested();

        // Never touch a device before both LE links are known to be up.
        if (!bothLeConnected)
            return new(LeAudioEndpointOutcome.BlockedLeLinks, "both LE links must be connected first");

        var inspection = await InspectAsync(primary, platform, cancellationToken).ConfigureAwait(false);
        if (inspection.State == LeAudioAudioEndpointState.Unknown && (inspection.ClassicA2dpRender is not null || inspection.LeAudioRender is not null))
            return new(LeAudioEndpointOutcome.Failed, inspection.Detail, inspection);
        if (inspection.State == LeAudioAudioEndpointState.Ambiguous)
            return new(LeAudioEndpointOutcome.Ambiguous, inspection.Detail, inspection);
        // Missing LE Audio must win over "Classic already disabled": otherwise a hidden Classic
        // endpoint with no LE render endpoint would be reported as a successful LE-only state.
        if (!inspection.LeAudioRenderPresent)
            return new(LeAudioEndpointOutcome.BlockedLeAudioEndpointMissing, inspection.Detail, inspection);
        if (inspection.ClassicA2dpRender is null)
            return new(LeAudioEndpointOutcome.NoClassicEndpoint, inspection.ClassicHfpRender is { IsDisabled: false } ? "Classic HFP render endpoint remains" : null, inspection);
        if (inspection.ClassicA2dpDisabled)
        {
            var alreadyDefault = await platform.SetDefaultAsync(inspection.LeAudioRender!, cancellationToken).ConfigureAwait(false);
            return alreadyDefault.Success
                ? new(LeAudioEndpointOutcome.AlreadyLeAudioOnly, null, inspection)
                : new(LeAudioEndpointOutcome.Failed, alreadyDefault.Detail, inspection);
        }

        // Route media to LE before hiding Classic: if this fails, do not hide anything, otherwise
        // Windows could fall back to the desktop speakers and the user would still hear one ear/no audio.
        var defaultResult = await platform.SetDefaultAsync(inspection.LeAudioRender!, cancellationToken).ConfigureAwait(false);
        if (!defaultResult.Success)
            return new(LeAudioEndpointOutcome.Failed, defaultResult.Detail, inspection);

        cancellationToken.ThrowIfCancellationRequested();
        var operation = await platform.SetEnabledAsync(inspection.ClassicA2dpRender, false, cancellationToken).ConfigureAwait(false);
        if (!operation.Success)
            return new(LeAudioEndpointOutcome.Failed, operation.Detail, inspection);

        var after = await InspectAsync(primary, platform, cancellationToken).ConfigureAwait(false);
        if (!after.LeAudioRenderPresent)
        {
            // LE may disappear between inspection and the visibility change. Restore the output
            // we just disabled rather than leaving the user without an available render path.
            using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await platform.SetEnabledAsync(inspection.ClassicA2dpRender, true, rollback.Token).ConfigureAwait(false);
            return new(LeAudioEndpointOutcome.Failed, "LE Audio render endpoint became unavailable; Classic restore was requested", after);
        }
        return after.ClassicA2dpDisabled
            ? new(LeAudioEndpointOutcome.Disabled, null, after)
            : new(LeAudioEndpointOutcome.Failed, "Windows did not confirm the Classic A2DP endpoint disabled", after);
    }

    public static Task<LeAudioEndpointResult> RestoreAsync(ulong primary, CancellationToken cancellationToken = default) =>
        RestoreAsync(primary, new WindowsAudioEndpointPlatform(), cancellationToken);

    public static async Task<LeAudioEndpointResult> RestoreAsync(
        ulong primary, IAudioEndpointPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        cancellationToken.ThrowIfCancellationRequested();
        var inspection = await InspectAsync(primary, platform, cancellationToken).ConfigureAwait(false);
        if (inspection.State == LeAudioAudioEndpointState.Ambiguous && inspection.ClassicA2dpRender is null && inspection.ClassicHfpRender is null
            || inspection.State == LeAudioAudioEndpointState.Unknown
            && (inspection.ClassicA2dpRender is not null || inspection.ClassicHfpRender is not null))
            return new(LeAudioEndpointOutcome.Failed, inspection.Detail, inspection);
        var disabled = new List<AudioEndpointInfo>();
        if (inspection.ClassicA2dpRender is { IsDisabled: true } a2dp) disabled.Add(a2dp);
        if (inspection.ClassicHfpRender is { IsDisabled: true } hfp) disabled.Add(hfp);
        if (disabled.Count == 0)
            return new(LeAudioEndpointOutcome.NothingToRestore, null, inspection);

        foreach (var endpoint in disabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = await platform.SetEnabledAsync(endpoint, true, cancellationToken).ConfigureAwait(false);
            if (!operation.Success)
                return new(LeAudioEndpointOutcome.Failed, operation.Detail, inspection);
        }

        var after = await InspectAsync(primary, platform, cancellationToken).ConfigureAwait(false);
        bool stillDisabled = disabled.Any(e => !new[] { after.ClassicA2dpRender, after.ClassicHfpRender }
                .Any(restored => restored?.InstanceId == e.InstanceId && restored is { IsDisabled: false, IsStateKnown: true }));
        return stillDisabled
            ? new(LeAudioEndpointOutcome.Failed, "Windows did not confirm the Classic endpoint re-enabled", after)
            : new(LeAudioEndpointOutcome.Enabled, null, after);
    }

    /// <summary>
    /// "扬声器 (3- OPPO Enco X4)" and "耳机 (OPPO Enco X4)" both normalize to "OPPO Enco X4";
    /// the leading "3- " is the Windows endpoint instance counter, not part of the product name.
    /// </summary>
    public static string NormalizeEndpointName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string text = name.Trim();
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open >= 0 && close > open) text = text[(open + 1)..close].Trim();

        int dash = text.IndexOf('-');
        if (dash is > 0 and <= 3 && text[..dash].All(char.IsDigit)) text = text[(dash + 1)..].Trim();
        return text;
    }
}
