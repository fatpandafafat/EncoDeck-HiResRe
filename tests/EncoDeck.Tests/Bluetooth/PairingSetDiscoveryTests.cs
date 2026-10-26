// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;

namespace EncoDeck.Tests.Bluetooth;

public class PairingSetDiscoveryTests
{
    [Fact]
    public async Task NativeCompletionBeforeCallbackArrival_DoesNotPermitFallback()
    {
        var discovery = new PairingSetDiscovery();
        // Native primary PairAsync has returned, but Windows has not yet dispatched the event.
        var finish = discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(finish.IsCompleted);
        using (var callback = discovery.TryEnter())
        {
            Assert.NotNull(callback);
            Assert.False(finish.IsCompleted);
        }
        Assert.True(await finish);
        Assert.Null(discovery.TryEnter());
    }

    [Fact]
    public async Task CallbackEnteredBeforeMemberEnumeration_FallbackWaitsForEntireCallback()
    {
        var discovery = new PairingSetDiscovery();
        var callback = discovery.TryEnter();
        Assert.NotNull(callback);
        // Reproduces the captured log: discovery logged members=1, member.Id/PairAsync
        // hasn't completed, and the old implementation took an empty member-task snapshot.
        var finish = discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(finish.IsCompleted);
        callback.Dispose();
        Assert.True(await finish);
    }

    [Fact]
    public async Task MultipleAdmittedCallbacks_AllMustFinishBeforeFallback()
    {
        var discovery = new PairingSetDiscovery();
        var first = discovery.TryEnter();
        var second = discovery.TryEnter();
        Assert.NotNull(first);
        Assert.NotNull(second);
        var finish = discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        first.Dispose();
        first.Dispose(); // disposal must not decrement twice and release the gate prematurely
        Assert.False(finish.IsCompleted);
        second.Dispose();
        Assert.True(await finish);
    }

    [Fact]
    public async Task NoEvent_ClosesAdmissionBeforeAllowingExplicitFallback()
    {
        var discovery = new PairingSetDiscovery();
        Assert.False(await discovery.WaitAndCloseAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
        // A stale callback dispatched after the fallback decision cannot start another PairAsync.
        Assert.Null(discovery.TryEnter());
    }

    [Fact]
    public async Task CancellationWhileMemberIsPending_ThrowsInsteadOfAllowingFallback()
    {
        var discovery = new PairingSetDiscovery();
        using var callback = discovery.TryEnter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var finish = discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        Assert.Null(discovery.TryEnter());
    }
}
