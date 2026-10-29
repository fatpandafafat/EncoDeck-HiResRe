// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.LeAudio;

namespace EncoDeck.Tests.Bluetooth;

public class PairingSetPreparationTests
{
    [Fact]
    public async Task EarlyMember_WaitsForPrimaryAndTransportReportBeforeDiscoveryDrains()
    {
        var ct = TestContext.Current.CancellationToken;
        var preparation = new PairingSetPreparation();
        var discovery = new PairingSetDiscovery();
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool memberPaired = false;
        var callback = discovery.TryEnter();
        Assert.NotNull(callback);
        async Task MemberAsync()
        {
            using (callback)
                memberPaired = await preparation.WaitAsync(ct);
        }
        var member = MemberAsync();
        var drain = discovery.WaitAndCloseAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(member.IsCompleted);
        var primary = preparation.CompleteAsync(true, async token =>
        {
            reportStarted.SetResult();
            await reportFinished.Task.WaitAsync(token);
        }, ct);
        await reportStarted.Task.WaitAsync(ct);
        Assert.False(member.IsCompleted);
        Assert.False(drain.IsCompleted);
        reportFinished.SetResult();
        await primary;
        await member;
        Assert.True(memberPaired);
        Assert.True(await drain);
    }

    [Fact]
    public async Task FailedPrimary_SkipsTransportReportAndReleasesMemberWithoutPairing()
    {
        var preparation = new PairingSetPreparation();
        var ct = TestContext.Current.CancellationToken;
        var member = preparation.WaitAsync(ct);
        await preparation.CompleteAsync(false, _ => throw new InvalidOperationException("must not report"), ct);
        Assert.False(await member);
    }

    [Fact]
    public async Task PreparationFailure_ReleasesPendingMemberWithoutPairing()
    {
        var preparation = new PairingSetPreparation();
        var ct = TestContext.Current.CancellationToken;
        var member = preparation.WaitAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.CompleteAsync(true,
            _ => throw new InvalidOperationException("control failed"), ct));
        Assert.False(await member);
    }

    [Fact]
    public async Task CancellationDuringPreparation_DoesNotReleaseMembersForPairing()
    {
        var preparation = new PairingSetPreparation();
        var ct = TestContext.Current.CancellationToken;
        var member = preparation.WaitAsync(ct);
        using var canceled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.CompleteAsync(true,
            async token => { canceled.Cancel(); await Task.Delay(Timeout.Infinite, token); }, canceled.Token));
        Assert.False(await member);
    }

    [Fact]
    public async Task PrimaryWithoutPreparation_AllowsMemberPairing()
    {
        var preparation = new PairingSetPreparation();
        var ct = TestContext.Current.CancellationToken;
        await preparation.CompleteAsync(true, null, ct);
        Assert.True(await preparation.WaitAsync(ct));
    }
}
