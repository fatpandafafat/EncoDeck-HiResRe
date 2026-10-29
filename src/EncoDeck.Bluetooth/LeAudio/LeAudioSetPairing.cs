// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Privacy;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>
/// One bounded set-pairing attempt for the other earbud. Windows reports set members it found through the
/// CSIS protocol (RSI resolved with the primary's SIRK), so those are the pair's own earbud even when their
/// address differs from the scan-response hint: the X4 advertises the peer at that public address, while the
/// Enco X3's LE Audio advert uses a private address and its hint may be the peer's Classic address.
/// </summary>
internal sealed class LeAudioSetPairing(ulong primary, ulong secondary, Action<string> log,
    Func<CancellationToken, Task>? prepareMembers = null)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private int _events;
    private int _paired;
    private int _rejected;
    private long _pairedMember;

    /// <summary>Address of the set member this attempt bonded (its identity once bonded), or null.</summary>
    public ulong? PairedMember => Interlocked.Read(ref _pairedMember) is var a && a != 0 ? (ulong)a : null;

    internal bool RequiresPreparation => prepareMembers is not null;

    internal enum MemberKind { Rejected, AdvertisedPeer, SetMember }

    /// <summary>
    /// The advertised peer is preferred; any other LE member Windows discovered is accepted too, but never the
    /// primary itself, a Classic node or an id without an address.
    /// </summary>
    internal static MemberKind Classify(string id, ulong primary, ulong secondary)
    {
        if (!id.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase)) return MemberKind.Rejected;
        int dash = id.LastIndexOf('-');
        if (dash < 0 || !TryParseAddress(id.AsSpan(dash + 1), out ulong address) || address == primary) return MemberKind.Rejected;
        return address == secondary && secondary != primary ? MemberKind.AdvertisedPeer : MemberKind.SetMember;
    }

    private static bool TryParseAddress(ReadOnlySpan<char> text, out ulong address)
    {
        address = 0;
        if (text.Length != 17) return false;
        for (int i = 0; i < 17; i++)
        {
            char c = text[i];
            if (i % 3 == 2) { if (c != ':') return false; continue; }
            int digit = c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : -1;
            if (digit < 0) return false;
            address = (address << 4) | (uint)digit;
        }
        return address != 0;
    }

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
        var preparation = new PairingSetPreparation();
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
                        var kind = Classify(id, primary, secondary);
                        if (kind == MemberKind.Rejected || (kind == MemberKind.SetMember && Volatile.Read(ref _paired) > 0))
                        {
                            Interlocked.Increment(ref _rejected);
                            log($"SET ignored member: {(kind == MemberKind.Rejected ? "not an LE earbud of this set" : "the other earbud is already paired")}");
                            continue;
                        }
                        log(kind == MemberKind.AdvertisedPeer ? "SET member is the advertised peer" : "SET member differs from the advertised peer address; accepted as discovered by the set protocol");
                        lock (_gate) { if (!_claimed.Add(id)) continue; }
                        stage = "read-member-pairing";
                        var memberPairing = member.Pairing;
                        bool alreadyPaired = memberPairing.IsPaired;
                        log($"SET expected peer admitted; alreadyPaired={alreadyPaired}");
                        if (prepareMembers is not null && !alreadyPaired)
                        {
                            stage = "wait-primary-preparation";
                            log("SET member waiting for primary LE transport preparation");
                            if (!await preparation.WaitAsync(ct).ConfigureAwait(false))
                            {
                                lock (_gate) _claimed.Remove(id);
                                return;
                            }
                        }
                        stage = "pair-member";
                        var status = alreadyPaired ? DevicePairingResultStatus.AlreadyPaired
                            : await PairAsync(memberPairing, level, ct, discoverMembers: false).ConfigureAwait(false);
                        if (status == DevicePairingResultStatus.OperationAlreadyInProgress)
                            status = await LeAudioPairing.WaitForExistingPairingAsync(secondary, ct, log).ConfigureAwait(false);
                        if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                        {
                            Interlocked.Increment(ref _paired);
                            Interlocked.CompareExchange(ref _pairedMember, (long)await MemberAddressAsync(id).ConfigureAwait(false), 0);
                        }
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
            if (discoverMembers)
                await preparation.CompleteAsync(
                    status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired,
                    prepareMembers, ct).ConfigureAwait(false);
            if (supported)
            {
                log($"SET primary pairing returned {status}; waiting for discovery callback and member completion");
                // The X4 reports its set within seconds; the X3's other earbud advertises on its own
                // (RSI, private address) and may start later, so allow Windows' RSI scan more time.
                bool observed = await discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
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
            preparation.Stop();
            discovery.Close();
            custom.PairingRequested -= OnRequested;
            if (supported) custom.PairingSetMembersRequested -= OnMembers;
        }
    }

    /// <summary>The bonded member's address; after bonding Windows reports the identity address, not the private one.</summary>
    private static async Task<ulong> MemberAddressAsync(string id)
    {
        try
        {
            using var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(id);
            if (device is not null) return device.BluetoothAddress;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { }
        int dash = id.LastIndexOf('-');
        return dash >= 0 && TryParseAddress(id.AsSpan(dash + 1), out ulong address) ? address : 0;
    }

    public void LogSummary()
    {
        log($"SET summary: events={Volatile.Read(ref _events)}; expectedPeerPaired={Volatile.Read(ref _paired)}; ignored={Volatile.Read(ref _rejected)}; synchronization=unverified");
    }
}
