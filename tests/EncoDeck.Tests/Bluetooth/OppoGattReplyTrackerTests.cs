// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Gatt;
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Bluetooth;

public class OppoGattReplyTrackerTests
{
    [Fact]
    public void HostReplies_DoNotRequireAnEarbudReply()
    {
        var tracker = new OppoGattReplyTracker();
        tracker.Sent(new OppoFrame(OppoCommand.ResponseOf(OppoCommand.TimeRequest), 1, [0]).Encode(), 0);
        Assert.False(tracker.IsSilent(10_000));
    }

    [Fact]
    public void UnacknowledgedHostCapabilities_DoNotTriggerPeerHandover()
    {
        var tracker = new OppoGattReplyTracker();
        tracker.Sent(new OppoFrame(OppoCommand.SetHostCapabilities, 1, [0]).Encode(), 0);
        Assert.False(tracker.IsSilent(10_000));
    }

    [Fact]
    public void NewRequestsDoNotHideSilence_AndEventsDoNotCountAsResponses()
    {
        var tracker = new OppoGattReplyTracker();
        tracker.Sent(new OppoFrame(OppoCommand.Handshake, 1, []).Encode(), 0);
        tracker.Sent(new OppoFrame(OppoCommand.QueryBattery, 2, []).Encode(), 2500);
        Assert.False(tracker.Received(new OppoFrame(OppoCommand.DeviceEvent, 3, [1, 0]).Encode()));
        Assert.True(tracker.IsSilent(3000));
        Assert.True(tracker.Received(new OppoFrame(OppoCommand.ResponseOf(OppoCommand.QueryBattery), 2, [0, 0]).Encode()));
        Assert.False(tracker.IsSilent(10_000));
    }

    [Fact]
    public void AlternateEventCodeResponseConfirmsThePeer()
    {
        var tracker = new OppoGattReplyTracker();
        tracker.Sent(new OppoFrame(OppoCommand.QueryEventCodes, 1, []).Encode(), 10);
        Assert.True(tracker.Received(new OppoFrame(OppoCommand.EventCodesAltResponse, 1, [0, 0]).Encode()));
        Assert.False(tracker.IsSilent(10_000));
    }

    [Fact]
    public void FragmentedPacketIsTrackedAfterItsLastFragment()
    {
        var tracker = new OppoGattReplyTracker();
        var parts = new OppoFrame(OppoCommand.SetCustomEqualizer, 1, new byte[1200]).EncodeLinkFrames();
        tracker.Sent(parts[0], 0);
        Assert.False(tracker.IsSilent(5000));
        foreach (var part in parts.Skip(1)) tracker.Sent(part, 5000);
        Assert.False(tracker.IsSilent(7999));
        Assert.True(tracker.IsSilent(8000));
    }
}
