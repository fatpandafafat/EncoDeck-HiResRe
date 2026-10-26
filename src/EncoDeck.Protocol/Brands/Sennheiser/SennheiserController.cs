// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Protocol.Brands.Sennheiser;

internal sealed partial class SennheiserController(IDuplexByteChannel channel, ControllerContext context, SennheiserProfile profile, bool earbuds)
    : RequestReplyController(channel, context)
{
    private readonly SennheiserFrameCodec _codec = new();
    private SennheiserLive _live = new();
    private bool _sliderReady;
    private int? _firmwareMajor;
    protected override IReadOnlyList<BrandPacket> Decode(ReadOnlySpan<byte> bytes) => _codec.Feed(bytes);
    private bool Has(SennheiserTraits trait) => profile.Traits.HasFlag(trait);
    private Task<BrandPacket?> Exchange(ushort command, byte[] payload, Func<byte[], bool> valid, CancellationToken ct) =>
        ExchangeAsync(SennheiserFrameCodec.Encode(command, payload), p =>
            p.Command == (command | 0x180) || (p.Command == (command | 0x100) && valid(p.Payload)), ct);
    private async Task<BrandPacket?> Query(ushort command, byte[] payload, Func<byte[], bool> valid, CancellationToken ct)
    {
        var reply = await Exchange(command, payload, valid, ct).ConfigureAwait(false);
        return reply?.Type == 2 ? reply : null;
    }
    protected override async Task<bool> StartSessionAsync(CancellationToken ct)
    {
        // Registration makes the device push the current values of each feature as notifications.
        foreach (byte feature in profile.NotificationFeatures)
            await Query(0x0007, [feature], p => p.Length == 0, ct).ConfigureAwait(false);
        if (!(await RefreshAsync(ct).ConfigureAwait(false)).IsSuccess) return false;
        StartPolling();
        return true;
    }
    public override async Task<CommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await Operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Closed.IsCompleted) return CommandResult.NotConnected;
            var battery = await Query(0x0603, [], ValidBattery, cancellationToken).ConfigureAwait(false);
            if (battery is null) return TimedOut;
            await Query(0x0602, [], p => p.Length is >= 1 and <= 3 && p.All(v => v <= 2 || v == 255), cancellationToken).ConfigureAwait(false);
            await Query(0x1201, [], p => p.Length == 6, cancellationToken).ConfigureAwait(false);
            // HDB 630 reports its version as three bytes under 0x1202; only asked when 0x1201 gave nothing.
            if (_firmwareMajor is null) await Query(0x1202, [], p => p.Length == 3, cancellationToken).ConfigureAwait(false);
            await Query(0x1206, [], p => p.Length is > 0 and <= 100, cancellationToken).ConfigureAwait(false);
            await RefreshNoiseAsync(cancellationToken).ConfigureAwait(false);
            await RefreshControlsAsync(cancellationToken).ConfigureAwait(false);
            await RefreshEqualizerAsync(cancellationToken).ConfigureAwait(false);
            await RefreshPairedDevicesAsync(cancellationToken).ConfigureAwait(false);
            await RefreshFindAsync(cancellationToken).ConfigureAwait(false);
            PublishProfile();
            return CommandResult.Ok;
        }
        finally { Operations.Release(); }
    }
    private void PublishProfile() => Store.TryUpdate(DeviceId, s => s with { Profile = profile.WithLive(_live) });
    private bool ValidBattery(byte[] p) => p.Length >= (earbuds ? 2 : 1) && p.Length <= (earbuds ? 3 : 1)
        && p.All(v => v <= 100 || v == 255) && p.Any(v => v <= 100);
    protected override void HandlePacket(BrandPacket packet)
    {
        if (packet.Type is not (1 or 2)) return;
        ushort command = (ushort)(packet.Command & ~0x180);
        if (HandleNoisePacket(packet, command) || HandleControlPacket(packet, command) || HandleEqualizerPacket(packet, command)
            || HandleDevicePacket(packet, command) || HandleBasicPacket(command, packet.Payload)) Touch();
    }
    private bool HandleBasicPacket(ushort command, byte[] p)
    {
        if (command == 0x0603 && ValidBattery(p))
        {
            for (int i = 0; i < p.Length; i++) if (p[i] <= 100)
            {
                int index = i;
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With((BatteryComponent)index,
                    Observed.From(new BatteryReading(p[index], s.Battery.Get((BatteryComponent)index)?.Value.IsCharging), Now, StateSource.QueryResponse)) });
            }
        }
        else if (command == 0x0602 && p.Length is >= 1 and <= 3 && p.All(v => v <= 2 || v == 255))
        {
            for (int i = 0; i < p.Length; i++) if (p[i] <= 2)
            {
                int index = i;
                Store.TryUpdate(DeviceId, s => s with { Battery = s.Battery.With((BatteryComponent)index,
                    Observed.From(new BatteryReading(s.Battery.Get((BatteryComponent)index)?.Value.Level, p[index] == 1), Now, StateSource.QueryResponse)) });
            }
        }
        // Status-based models never read the headband level as a mode: it is an ANC amount there.
        else if (Has(SennheiserTraits.Slider) && !_live.StatusNoise && command == 0x1A03 && p is [<= 100])
            Store.TryUpdate(DeviceId, s => s with { NoiseControl = Observed.From(new NoiseControlState(p[0] == 0 ? NoiseControlMode.NoiseCancellation : NoiseControlMode.Transparency,
                p[0] is 0 or 100 ? null : "percent:" + p[0]), Now, StateSource.QueryResponse) });
        else if (command == 0x1201 && p.Length == 6)
        {
            _firmwareMajor = BinaryPrimitives.ReadUInt16BigEndian(p);
            SetFirmware($"{_firmwareMajor}.{BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2))}.{BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4))}");
        }
        else if (command == 0x1202 && p.Length == 3 && _firmwareMajor is null)
        {
            _firmwareMajor = p[0];
            SetFirmware($"{p[0]}.{p[1]}.{p[2]}");
        }
        else if (command == 0x1206 && ModelCode(p) is { } model)
            Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From((s.Firmware?.Value ?? FirmwareInfo.Empty) with { ModelCode = model }, Now, StateSource.QueryResponse) });
        else return false;
        return true;
    }
    /// <summary>Printable model id; real replies end with a NUL terminator (e.g. "M4AEBT Black\0").</summary>
    internal static string? ModelCode(byte[] p)
    {
        var text = p.AsSpan().TrimEnd((byte)0);
        if (p.Length > 100 || text.IsEmpty) return null;
        foreach (byte b in text) if (b is < 32 or > 126) return null;
        return Encoding.ASCII.GetString(text);
    }
    private void SetFirmware(string version) =>
        Store.TryUpdate(DeviceId, s => s with { Firmware = Observed.From((s.Firmware?.Value ?? FirmwareInfo.Empty) with { Version = version }, Now, StateSource.QueryResponse) });
    private async Task<CommandResult> SetValueAsync(ushort command, byte[] value, CancellationToken ct)
    {
        var reply = await Exchange(command, value, p => p.Length == 0, ct).ConfigureAwait(false);
        return reply is null ? TimedOut : reply.Type == 3
            ? new(CommandStatus.Rejected, $"The device rejected command {command:X4}.") : CommandResult.Ok;
    }
}
