// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>An LE peripheral address; <see cref="IsRandom"/> distinguishes random from public addresses.</summary>
internal sealed record LeTarget(ulong Address, bool IsRandom);

/// <summary>One advertiser seen during a bounded scan (name and service UUIDs merged across its packets).</summary>
internal sealed record LeAdvertisement(LeTarget Target, string? Name, IReadOnlyList<Guid> ServiceUuids);

/// <summary>
/// Process-lifetime map from a classic headset address to the separate LE peripheral that carries its
/// control service. Kept in memory only, so no Bluetooth address is written to the settings file.
/// </summary>
internal sealed class LeCompanionCache
{
    public static LeCompanionCache Shared { get; } = new();

    private readonly ConcurrentDictionary<ulong, LeTarget> _targets = new();

    public bool TryGet(ulong classicAddress, out LeTarget target) => _targets.TryGetValue(classicAddress, out target!);

    public void Remember(ulong classicAddress, LeTarget target) => _targets[classicAddress] = target;

    public void Forget(ulong classicAddress) => _targets.TryRemove(classicAddress, out _);
}

/// <summary>
/// Bounded fallback for headsets whose GATT control service lives on a separate LE peripheral (AKG
/// N400NC family, AKG N5 / N9 Hybrid "-LE"). Used only after the classic address itself has no
/// control endpoint, and binds only when exactly one advertiser matches the paired device's name.
/// </summary>
internal static class LeCompanionResolver
{
    public static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(4);

    /// <summary>Cap on the classic-address attempt before scanning, so a first connect takes about 8 s at worst.</summary>
    public static readonly TimeSpan ClassicAttemptCap = TimeSpan.FromSeconds(4);

    /// <summary>Case-, space- and punctuation-insensitive form of a device name.</summary>
    public static string Normalize(string? name) => new((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The advertised name equals the classic name, or the classic name followed by "-LE".</summary>
    public static bool NameMatches(string classicName, string? advertisedName)
    {
        string expected = Normalize(classicName), actual = Normalize(advertisedName);
        return expected.Length > 0 && (actual == expected || actual == expected + "le");
    }

    /// <summary>
    /// Advertisers whose name matches and that do not advertise a conflicting service list (an
    /// advertiser listing service UUIDs must include <paramref name="service"/>). Returns the single
    /// match, or null with the number of matches when there is none or more than one.
    /// </summary>
    public static (LeTarget? Target, int Count) Select(string classicName, Guid service, IEnumerable<LeAdvertisement> advertisements)
    {
        var matches = advertisements
            .Where(a => NameMatches(classicName, a.Name) && (a.ServiceUuids.Count == 0 || a.ServiceUuids.Contains(service)))
            .Select(a => a.Target).Distinct().ToArray();
        return (matches.Length == 1 ? matches[0] : null, matches.Length);
    }

    /// <summary>The classic address has no usable GATT control endpoint (as opposed to access or cancellation problems).</summary>
    private static bool IsMissingEndpoint(ChannelOpenStatus status) =>
        status is ChannelOpenStatus.ControlEndpointNotFound or ChannelOpenStatus.ServiceNotFound or ChannelOpenStatus.DeviceUnreachable or ChannelOpenStatus.ConnectFailed;

    /// <param name="open">Opens the endpoint at a target (null = the classic address); a non-null name must match the peer's reported name.</param>
    /// <param name="scan">Bounded LE advertisement scan.</param>
    public static async Task<ChannelOpenResult> OpenAsync(ulong classicAddress, string? classicName, GattStreamEndpoint endpoint, LeCompanionCache cache,
        Func<LeTarget?, string?, CancellationToken, Task<ChannelOpenResult>> open, Func<CancellationToken, Task<IReadOnlyList<LeAdvertisement>>> scan,
        ILogger logger, CancellationToken cancellationToken, TimeSpan? classicAttemptCap = null)
    {
        if (!endpoint.NameMatchedLeFallback || string.IsNullOrWhiteSpace(classicName))
            return await open(null, null, cancellationToken).ConfigureAwait(false);

        if (cache.TryGet(classicAddress, out var cached))
        {
            var result = await open(cached, classicName, cancellationToken).ConfigureAwait(false);
            if (result.Status == ChannelOpenStatus.Opened) return result;
            cache.Forget(classicAddress);
            logger.LogInformation("{Endpoint}: remembered LE endpoint failed ({Detail}); cleared", endpoint.Name, result.Detail);
        }

        ChannelOpenResult classic;
        using (var cap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // The opener disposes its LE device and service when the cap cancels it.
            cap.CancelAfter(classicAttemptCap ?? ClassicAttemptCap);
            try
            {
                classic = await open(null, null, cap.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                classic = ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name}: no answer at the classic address within {(classicAttemptCap ?? ClassicAttemptCap).TotalSeconds:0} s.");
            }
        }
        if (classic.Status == ChannelOpenStatus.Opened || !IsMissingEndpoint(classic.Status)) return classic;

        var (target, count) = Select(classicName, endpoint.Service, await scan(cancellationToken).ConfigureAwait(false));
        logger.LogInformation("{Endpoint}: {Count} LE advertiser(s) matched the device name", endpoint.Name, count);
        if (target is null)
            return ChannelOpenResult.Failed(classic.Status, $"{classic.Detail}; {count} LE advertiser(s) matched the device name.");
        var companion = await open(target, classicName, cancellationToken).ConfigureAwait(false);
        if (companion.Status == ChannelOpenStatus.Opened) cache.Remember(classicAddress, target);
        return companion;
    }
}
