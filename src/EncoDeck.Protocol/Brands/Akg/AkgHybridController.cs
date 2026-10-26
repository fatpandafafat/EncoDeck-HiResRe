// SPDX-License-Identifier: GPL-3.0-only
// Facts only (no code copied): battery 0x0CD6, charging 0x0009, case battery 0x000B and the
// secondary-ear "status 02" acknowledgement were observed on a real AKG N9 Hybrid by kalicyh/Raito
// (5804b65); the ANC write 0x0E06 / read 0x0901 values come from a JBL Tune 770NC RACE note
// (gist wielkiborsuk/f46f6792…) and are unverified on AKG, so they are experimental opt-in only.
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Akg;

internal sealed class AkgHybridController(IDuplexByteChannel channel, ControllerContext context, bool earbuds)
    : RequestReplyController(channel, context)
{
    private const ushort BatteryLevel = 0x0CD6, Charging = 0x0009, CaseBattery = 0x000B, AgentChannel = 0x0CD4, AncRead = 0x0901, AncWrite = 0x0E06;
    private readonly RaceFrameCodec _codec = new();
    private bool? _agentRight;
    private readonly bool?[] _charging = new bool?[2];
    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) => _codec.Feed(bytes);
    protected override async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        bool ready = (await RefreshAsync(ct).ConfigureAwait(false)).IsSuccess;
        if (ready) StartPolling();
        return ready;
    }

    /// <summary>
    /// Waits for the typed reply, or for an acknowledgement (type 0x5B) with a non-zero status, which
    /// is how a role without data answers (an N9 Hybrid answers the partner battery query with status 02).
    /// </summary>
    private Task<BrandPacket?> Query(ushort command, byte[] payload, byte type, Func<byte[], bool> valid, CancellationToken ct) =>
        ExchangeAsync(RaceFrameCodec.Encode(command, payload), p => p.Command == command
            && ((p.Type == type && valid(p.Payload)) || (p.Type == 0x5B && type != 0x5B && p.Payload is [> 0])), ct);

    private static bool Answered(BrandPacket? packet, byte type) => packet is not null && packet.Type == type;

    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            _agentRight = null; // Agent/partner may swap whenever a bud is returned to the case.
            if (earbuds) await Query(AgentChannel, [], 0x5B, p => p is [0, 0 or 1], cancellationToken).ConfigureAwait(false);
            bool battery = false;
            foreach (byte role in earbuds ? new byte[] { 0, 1 } : new byte[] { 0 })
            {
                // Charging first (the order Raito observed); the battery reading then carries the flag.
                await Query(Charging, [role], 0x5D, p => p.Length == 3 && p[0] == 0 && p[1] == role && p[2] <= 1, cancellationToken).ConfigureAwait(false);
                battery |= Answered(await Query(BatteryLevel, [role], 0x5D, p => p.Length == 3 && p[0] == 0 && p[1] == role && p[2] <= 100, cancellationToken).ConfigureAwait(false), 0x5D);
            }
            if (earbuds) await Query(CaseBattery, [], 0x5D, p => p.Length == 2 && p[0] == 0 && p[1] is > 0 and <= 100, cancellationToken).ConfigureAwait(false);
            await RefreshAnc(cancellationToken).ConfigureAwait(false);
            return battery && (!earbuds || _agentRight.HasValue) ? CommandResult.Ok : TimedOut;
        }
        finally { Operations.Release(); }
    }

    private async Task RefreshAnc(CancellationToken ct)
    {
        // ANC stays hidden unless the user enabled experimental features.
        if (!Context.AllowExperimental() || !Answered(await ReadAnc(ct).ConfigureAwait(false), 0x5B))
            Store.TryUpdate(DeviceId, s => s with { Profile = new AkgHybridProfile(earbuds), NoiseControl = null });
    }

    private Task<BrandPacket?> ReadAnc(CancellationToken ct) => ExchangeAsync(RaceFrameCodec.Encode(AncRead, [5, 0]),
        p => p.Command == AncRead && p.Type == 0x5B && TryAnc(p.Payload, out _), ct);

    /// <summary>
    /// <c>[05, 00, status, flags, filter, …]</c>: filter 0 / bit 0 clear = off, 1–4 noise cancelling,
    /// 5–7 (six-byte layout) or 9–11 (twelve-byte layout) ambient / talk-through.
    /// </summary>
    internal static bool TryAnc(byte[] p, out NoiseControlMode mode)
    {
        mode = NoiseControlMode.Off;
        if (!(p.Length is 9 or 15 && p[0] == 5 && p[1] == 0 && p[2] == 0 && (p.Length == 9 ? p[4] <= 7 : p[4] is <= 4 or >= 9 and <= 11))) return false;
        mode = (p[3] & 1) == 0 || p[4] == 0 ? NoiseControlMode.Off : p[4] >= (p.Length == 9 ? 5 : 9) ? NoiseControlMode.Transparency : NoiseControlMode.NoiseCancellation;
        return true;
    }

    protected override void HandlePacket(BrandPacket packet)
    {
        var p = packet.Payload;
        if (packet.Type == 0x5B && packet.Command == AgentChannel && p is [0, 0 or 1]) _agentRight = p[1] == 1;
        else if (packet.Type == 0x5D && packet.Command == Charging && p is [0, <= 1, <= 1]) _charging[p[1]] = p[2] == 1;
        else if (packet.Type == 0x5D && packet.Command == BatteryLevel && p is [0, <= 1, <= 100])
        {
            // Never label agent/partner as left/right without a confirmed channel response.
            if (earbuds && !_agentRight.HasValue) return;
            var component = !earbuds || (p[1] == 0 ? !_agentRight!.Value : _agentRight!.Value) ? BatteryComponent.Left : BatteryComponent.Right;
            Battery(component, p[2], _charging[p[1]]);
        }
        else if (packet.Type == 0x5D && packet.Command == CaseBattery && p is [0, > 0 and <= 100]) Battery(BatteryComponent.Case, p[1], null);
        else if (packet.Type == 0x5B && packet.Command == AncRead && Context.AllowExperimental() && TryAnc(p, out var mode))
        {
            Store.TryUpdate(DeviceId, s => s with { Profile = new AkgHybridProfile(earbuds, true), NoiseControl = Observed.From(new NoiseControlState(mode), Now, StateSource.QueryResponse) });
        }
        else return;
        Touch();
    }

    private void Battery(BatteryComponent component, int value, bool? charging) => Store.TryUpdate(DeviceId, s => s with
    { Battery = s.Battery.With(component, Observed.From(new BatteryReading(value, charging), Now, StateSource.QueryResponse)) });

    public override async Task<CommandResult> SetNoiseControlAsync(NoiseControlState target, CancellationToken cancellationToken)
    {
        // 0x0E06 payloads: [00 0A 01] noise cancelling, [00 0A 0A] ambient aware, [00 0B] off.
        byte[]? payload = target is { LevelId: null } ? target.Mode switch
        {
            NoiseControlMode.NoiseCancellation => [0, 0x0A, 1],
            NoiseControlMode.Transparency => [0, 0x0A, 0x0A],
            NoiseControlMode.Off => [0, 0x0B],
            _ => null,
        } : null;
        if (payload is null) return CommandResult.Unsupported("Unsupported ANC mode.");
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            if (!Context.AllowExperimental()) return CommandResult.Unsupported("AKG Hybrid noise control is experimental; enable experimental features first.");
            if (!Answered(await ReadAnc(cancellationToken).ConfigureAwait(false), 0x5B)) return CommandResult.Unsupported("ANC module layout has not been confirmed.");
            var ack = await ExchangeAsync(RaceFrameCodec.Encode(AncWrite, payload), p => p.Command == AncWrite && p.Type == 0x5B && p.Payload.Length >= 1, cancellationToken).ConfigureAwait(false);
            if (ack is not null && ack.Payload[0] != 0) return new(CommandStatus.Rejected, "Device rejected ANC change.");
            // Without an acknowledgement only the readback decides.
            if (!Answered(await ReadAnc(cancellationToken).ConfigureAwait(false), 0x5B)) return TimedOut;
            return Store.Current.NoiseControl?.Value.Mode == target.Mode ? CommandResult.Ok : new(CommandStatus.Rejected, "ANC readback did not match.");
        }
        finally { Operations.Release(); }
    }
}
