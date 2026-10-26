// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>Tracks unanswered requests without mistaking host replies or unsolicited events for a handshake.</summary>
internal sealed class OppoGattReplyTracker
{
    private readonly Lock _gate = new();
    private readonly OppoFrameDecoder _sent = new();
    private readonly OppoFrameDecoder _received = new();
    private readonly HashSet<ushort> _pending = [];
    private long? _unansweredSince;

    public void Sent(ReadOnlySpan<byte> data, long now)
    {
        lock (_gate)
        {
            foreach (var frame in _sent.Push(data))
            {
                // Host capability announcements need not be acknowledged by every model.
                if (frame.IsResponse || frame.Command == OppoCommand.SetHostCapabilities) continue;
                _pending.Add(frame.Command);
                _unansweredSince ??= now;
            }
        }
    }

    public bool Received(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            bool answered = false;
            foreach (var frame in _received.Push(data))
            {
                if (!frame.IsResponse || !(_pending.Contains(frame.BaseCommand)
                    || frame.Command == OppoCommand.EventCodesAltResponse && _pending.Contains(OppoCommand.QueryEventCodes))) continue;
                _pending.Clear();
                _unansweredSince = null;
                answered = true;
            }
            return answered;
        }
    }

    public bool IsSilent(long now)
    {
        lock (_gate) return _unansweredSince is { } since && now - since >= 3000;
    }
}
