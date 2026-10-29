// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;

namespace EncoDeck.Tests.Core;

public class LeAudioStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One of two bonded LE earbuds connected, control ready.</summary>
    private static readonly DeviceState OneEarbud = DeviceState.Empty with
    {
        Connection = DeviceConnectionState.ControlReady,
        IsLeAudio = true,
        IsLeAudioBonded = true,
        LeAudioLinks = new(PrimaryBonded: true, PrimaryConnected: false, SecondaryBonded: true, SecondaryConnected: true),
    };

    private static DeviceState WithWear(DeviceState state, bool? leftInCase, bool? rightInCase, StateSource source = StateSource.DeviceNotification) =>
        state with { Wear = Observed.From(new WearStatus(null, null, leftInCase, rightInCase, null), T0, source) };

    [Fact]
    public void OneEarbudWithoutWear_WarnsOnlyWhereWarningWithoutWear()
    {
        // Redmi Buds 8 Pro worn alone: no in-case report, so ordinary single-ear use stays quiet.
        Assert.False(LeAudioStatus.IsMissingEarbud(OneEarbud, warnWithoutWear: false));
        Assert.True(LeAudioStatus.IsMissingEarbud(OneEarbud, warnWithoutWear: true));
    }

    [Fact]
    public void OtherEarbudInCase_NeverWarns()
    {
        var state = WithWear(OneEarbud, leftInCase: true, rightInCase: false);
        Assert.False(LeAudioStatus.IsMissingEarbud(state, warnWithoutWear: true));
        Assert.False(LeAudioStatus.IsMissingEarbud(state, warnWithoutWear: false));
    }

    [Fact]
    public void BothEarbudsOutOfCase_Warns()
    {
        var state = WithWear(OneEarbud, leftInCase: false, rightInCase: false);
        Assert.True(LeAudioStatus.IsMissingEarbud(state, warnWithoutWear: false));
    }

    [Fact]
    public void CachedWear_IsNotEvidence()
    {
        var state = WithWear(OneEarbud, leftInCase: true, rightInCase: false, StateSource.Cache);
        Assert.True(LeAudioStatus.IsMissingEarbud(state, warnWithoutWear: true));
        Assert.False(LeAudioStatus.IsMissingEarbud(WithWear(OneEarbud, false, false, StateSource.Cache), warnWithoutWear: false));
    }

    [Fact]
    public void BothEarbudsConnected_DoesNotWarn()
    {
        var state = OneEarbud with { LeAudioLinks = OneEarbud.LeAudioLinks with { PrimaryConnected = true } };
        Assert.False(LeAudioStatus.IsMissingEarbud(state, warnWithoutWear: true));
    }

    [Theory]
    [InlineData(DeviceConnectionState.ControlConnecting)]
    [InlineData(DeviceConnectionState.Reconnecting)]
    [InlineData(DeviceConnectionState.ControlUnavailable)]
    public void NoLeLink_ClassicUp_WaitsOnlyWhenControlNeedsLeAudio(DeviceConnectionState connection)
    {
        var state = OneEarbud with { IsLeAudio = false, Connection = connection, LeAudioLinks = LeAudioLinkState.Empty };
        Assert.True(LeAudioStatus.IsWaitingForLeAudio(state, controlNeedsLeAudio: true));
        // Control over Classic SPP: show the real control state instead of "waiting for LC3".
        Assert.False(LeAudioStatus.IsWaitingForLeAudio(state, controlNeedsLeAudio: false));
    }

    [Theory]
    [InlineData(DeviceConnectionState.Disconnected)]
    [InlineData(DeviceConnectionState.PairedNotConnected)]
    public void NoLinkAtAll_WaitsForLeAudio(DeviceConnectionState connection)
    {
        var state = OneEarbud with { IsLeAudio = false, Connection = connection, LeAudioLinks = LeAudioLinkState.Empty };
        Assert.True(LeAudioStatus.IsWaitingForLeAudio(state, controlNeedsLeAudio: false));
        Assert.True(LeAudioStatus.IsWaitingForLeAudio(state, controlNeedsLeAudio: true));
    }

    [Fact]
    public void ControlReadyOrLeUp_NeverWaits()
    {
        var ready = OneEarbud with { IsLeAudio = false };
        Assert.False(LeAudioStatus.IsWaitingForLeAudio(ready, controlNeedsLeAudio: true));
        var leUp = OneEarbud with { Connection = DeviceConnectionState.ControlUnavailable };
        Assert.False(LeAudioStatus.IsWaitingForLeAudio(leUp, controlNeedsLeAudio: true));
    }
}
