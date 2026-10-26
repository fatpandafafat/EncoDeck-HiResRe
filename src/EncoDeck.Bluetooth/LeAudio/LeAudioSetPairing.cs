// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Privacy;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>One bounded set-pairing attempt, restricted to the peer identified in the X4 scan response.</summary>
internal sealed class LeAudioSetPairing(ulong primary, ulong secondary, Action<string> log)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private int _events;
    private int _paired;
    private int _rejected;

    internal static bool IsExpectedPeer(string id, ulong primary, ulong secondary) =>
        primary != secondary && secondary != 0 &&
        id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase) &&
        id.EndsWith("-" + new BluetoothAddress(secondary).ToFullString(), StringComparison.OrdinalIgnoreCase);

    // Some native WinRT vectors expose Count/GetAt but fail QI for the iterable interface.
    // Do not use foreach, LINQ, ToArray or List's enumerable constructor on the native vector.
    internal static T[] SnapshotByIndex<T>(IReadOnlyList<T> members)
    {
        var snapshot = new T[members.Count];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = members[i];
        return snapshot;
    }

    /// <param name="discoverMembers">False for a member Windows already reported: the two-earbud set is
    /// complete, so the member pairing neither subscribes to nor waits for another discovery event.</param>
    public async Task<DevicePairingResultStatus> PairAsync(DeviceInformationPairing pairing,
        DevicePairingProtectionLevel level, CancellationToken ct, bool discoverMembers = true)
    {
        var custom = pairing.Custom;
        var discovery = new PairingSetDiscovery();
        Exception? callbackError = null;
        bool supported = discoverMembers && ApiInformation.IsEventPresent(
            "Windows.Devices.Enumeration.DeviceInformationCustomPairing", "PairingSetMembersRequested");
        void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            if (!ct.IsCancellationRequested && args.PairingKind == DevicePairingKinds.ConfirmOnly) args.Accept();
        }
        void OnMembers(DeviceInformationCustomPairing sender, DevicePairingSetMembersRequestedEventArgs args)
        {
            // Enter before logging, enumerating members, or reading a COM property. Previously the
            // callback was counted only after member.Id: primary completion raced that gap.
            var callback = discovery.TryEnter();
            if (callback is null) return;
            // Start processing in the callback so a member request can start before it returns.
            // Never block event dispatch on GetResult(); the lifetime barrier below awaits the
            // asynchronous remainder, including any recursively discovered members.
            _ = ProcessMembersAsync(args, callback);
        }
        async Task ProcessMembersAsync(DevicePairingSetMembersRequestedEventArgs args, IDisposable callback)
        {
            using (callback)
            {
                string stage = "read-discovery-status";
                try
                {
                    Interlocked.Increment(ref _events);
                    log($"SET discovery={args.Status}; members={args.PairingSetMembers.Count}");
                    if (args.Status is not (DevicePairingAddPairingSetMemberStatus.SetDiscoveryCompletedByProtocol
                        or DevicePairingAddPairingSetMemberStatus.SetDiscoveryPartiallyCompletedByProtocol)) return;
                    stage = "snapshot-members-by-index";
                    var members = SnapshotByIndex(args.PairingSetMembers);
                    log($"SET indexed member snapshot ready; count={members.Length}");
                    foreach (var member in members)
                    {
                        ct.ThrowIfCancellationRequested();
                        stage = "read-member-id";
                        var id = member.Id;
                        if (!IsExpectedPeer(id, primary, secondary))
                        {
                            Interlocked.Increment(ref _rejected);
                            log("SET ignored member: not the expected LE peer");
                            continue;
                        }
                        lock (_gate) { if (!_claimed.Add(id)) continue; }
                        stage = "read-member-pairing";
                        var memberPairing = member.Pairing;
                        bool alreadyPaired = memberPairing.IsPaired;
                        log($"SET expected peer admitted; alreadyPaired={alreadyPaired}");
                        stage = "pair-member";
                        var status = alreadyPaired ? DevicePairingResultStatus.AlreadyPaired
                            : await PairAsync(memberPairing, level, ct, discoverMembers: false).ConfigureAwait(false);
                        if (status == DevicePairingResultStatus.OperationAlreadyInProgress)
                            status = await LeAudioPairing.WaitForExistingPairingAsync(secondary, ct, log).ConfigureAwait(false);
                        if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                            Interlocked.Increment(ref _paired);
                        else { lock (_gate) _claimed.Remove(id); }
                        log($"SET expected peer result={status}");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Interlocked.CompareExchange(ref callbackError, ex, null);
                    log($"SET callback failed: stage={stage}; {ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}; stack={ex.StackTrace}");
                }
            }
        }
        custom.PairingRequested += OnRequested;
        if (supported) custom.PairingSetMembersRequested += OnMembers;
        else if (discoverMembers) log("SET API unavailable; explicit LE address fallback remains enabled");
        try
        {
            var status = (await custom.PairAsync(DevicePairingKinds.ConfirmOnly, level)
                .AsTask(ct).ConfigureAwait(false)).Status;
            if (supported)
            {
                log($"SET primary pairing returned {status}; waiting for discovery callback and member completion");
                bool observed = await discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                log(observed ? "SET callback processing drained"
                    : "SET no callback within discovery window; callbacks closed before explicit fallback");
            }
            ct.ThrowIfCancellationRequested();
            if (callbackError is not null)
                throw new InvalidOperationException("Windows set-member pairing did not finish cleanly; stopped without overlapping explicit fallback.", callbackError);
            return status;
        }
        finally
        {
            discovery.Close();
            custom.PairingRequested -= OnRequested;
            if (supported) custom.PairingSetMembersRequested -= OnMembers;
        }
    }

    public void LogSummary()
    {
        log($"SET summary: events={Volatile.Read(ref _events)}; expectedPeerPaired={Volatile.Read(ref _paired)}; ignored={Volatile.Read(ref _rejected)}; synchronization=unverified");
    }
}
