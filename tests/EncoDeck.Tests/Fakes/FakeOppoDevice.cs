// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Oppo;

namespace EncoDeck.Tests.Fakes;

/// <summary>
/// Test-only emulation of an OPPO device behind a <see cref="FakeByteChannel"/>, modelled on the
/// Enco X4 captures and the official client's documented replies. Knobs let tests inject failures.
/// </summary>
internal sealed class FakeOppoDevice
{
    public FakeByteChannel Channel { get; } = new();

    public uint ProductId { get; set; } = 0x06EC10;

    /// <summary>Currently reported noise-control index.</summary>
    public int NoiseIndex { get; set; } = 3;

    public byte EqualizerPreset { get; set; }

    public Dictionary<byte, byte> Features { get; } = new() { [0x04] = 1, [0x06] = 1, [0x11] = 1, [0x28] = 0, [0x18] = 0, [0x3A] = 0, [0x37] = 1 };

    public byte[] Battery { get; set; } = [0x01, 0x64, 0x02, 0x64];

    /// <summary>0x8114 payload: [status][codec]; 01 01 is what the X4 answers over LE Audio.</summary>
    public byte[] CodecResponse { get; set; } = [0x00, 0x02];

    /// <summary>Wear flags per component (left, right, case): 0x07 = in ear, 0x04 = in case lid open.</summary>
    public byte[] Wear { get; set; } = [0x07, 0x07, 0x04];

    /// <summary>Capability bitmap after the status byte (default: the X4's).</summary>
    public byte[] Capabilities { get; set; } = Convert.FromHexString("FF775AEEEE8FA65F1700000000000000");

    public bool AnswerHandshake { get; set; } = true;

    /// <summary>Status byte returned for SET commands (0 = success).</summary>
    public byte SetStatus { get; set; }

    /// <summary>When false the device acknowledges a SET but does not apply it.</summary>
    public bool ApplySets { get; set; } = true;

    /// <summary>
    /// Feature switches take effect only after this many further status queries (models that
    /// apply a switch a moment after acknowledging it).
    /// </summary>
    public int FeatureSettleQueries { get; set; }

    private (byte Id, byte Value, int Remaining)? _pendingFeature;

    /// <summary>When true SET commands are swallowed (no acknowledgement).</summary>
    public bool DropSets { get; set; }

    public byte SpatialType { get; set; }

    public byte GameSoundType { get; set; }

    public byte PromptVolume { get; set; } = 6;

    public sbyte BassCurrent { get; set; }

    public byte HeadMotionType { get; set; }
    public bool LeBrSwitchEnabled { get; set; }
    public byte[]? LeBrSwitchResponse { get; set; }

    public uint CycleLeft { get; set; } = 0x0006;

    public uint CycleRight { get; set; } = 0x0006;

    /// <summary>Fit-test result to report after <c>0x0405 01</c>; null = never report.</summary>
    public (byte Left, byte Right)? FitResult { get; set; } = (1, 1);

    /// <summary>Gesture records as the X4 reports them (call rows per earbud, button 6).</summary>
    public List<(byte Side, byte Button, byte Action, byte Function)> Keys { get; } =
    [
        (1, 1, 1, 1), (1, 1, 2, 6), (1, 1, 3, 5), (1, 1, 4, 8), (1, 1, 5, 7),
        (2, 1, 1, 1), (2, 1, 2, 6), (2, 1, 3, 5), (2, 1, 4, 8), (2, 1, 5, 7),
        (1, 6, 1, 29), (1, 6, 2, 28), (1, 6, 3, 0),
        (2, 6, 1, 29), (2, 6, 2, 28), (2, 6, 3, 0),
    ];

    public List<(int Id, string Name, bool Selected, sbyte[] Gains)> CustomEq { get; } = [(4, "自定义1", false, new sbyte[10])];

    /// <summary>Source devices (address as sent on the wire, i.e. reversed).</summary>
    public List<(byte[] WireAddress, byte State, byte Flags, string Name)> Sources { get; } =
    [
        ([0x66, 0x55, 0x44, 0x33, 0x22, 0x11], 2, 0x17, "PC"),
        ([0x06, 0x05, 0x04, 0x03, 0x02, 0x01], 0, 0x08, "Phone"),
    ];

    public byte[]? PreferredWireAddress { get; set; }

    public List<OppoFrame> Received { get; } = [];

    /// <summary>Replies the fake received for device-initiated requests (e.g. 0x8500).</summary>
    public List<OppoFrame> HostReplies { get; } = [];

    private static readonly int[] EqBands = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>Maps a set index to the child index the device reports afterwards.</summary>
    public Func<int, int> ReportedIndexForSet { get; set; } = index => index switch
    {
        0 => 3,
        1 => 7,
        2 => 8,
        11 => 13,
        _ => index,
    };

    public FakeOppoDevice()
    {
        Channel.Responder = Respond;
    }

    public void PushNoiseEvent(int index) =>
        Channel.Receive(new OppoFrame(OppoCommand.DeviceEvent, 0x30, [0x03, 0x01, 0x01, .. Mask(index)]));

    public void PushBatteryEvent(params byte[] pairs) =>
        Channel.Receive(new OppoFrame(OppoCommand.DeviceEvent, 0x31, [0x01, (byte)(pairs.Length / 2), .. pairs]));

    public void PushWearEvent(byte left, byte right, byte box) =>
        Channel.Receive(new OppoFrame(OppoCommand.DeviceEvent, 0x32, [0x02, 0x03, 0x01, left, 0x02, right, 0x03, box]));

    /// <summary>Device-initiated request (e.g. 0x0500 time request) the host must answer.</summary>
    public void PushRequest(ushort command, params byte[] payload) =>
        Channel.Receive(new OppoFrame(command, 0x40, payload));

    private OppoFrame Ack(OppoFrame request) => new(OppoCommand.ResponseOf(request.Command), request.Sequence, [SetStatus]);

    private bool CanApply => SetStatus == 0 && ApplySets;

    private IEnumerable<OppoFrame>? Respond(OppoFrame request)
    {
        lock (Received) Received.Add(request);
        byte seq = request.Sequence;
        ushort rsp = OppoCommand.ResponseOf(request.Command);
        var p = request.Payload;

        if (request.IsResponse)
        {
            lock (HostReplies) HostReplies.Add(request);
            return null;
        }

        switch (request.Command)
        {
            case OppoCommand.Handshake:
                return AnswerHandshake ? [new OppoFrame(rsp, seq, [0x00, .. Capabilities])] : null;
            case OppoCommand.QueryProductId:
                return [new OppoFrame(rsp, seq, [0x00, (byte)ProductId, (byte)(ProductId >> 8), (byte)(ProductId >> 16)])];
            case OppoCommand.QueryColor:
                return [new OppoFrame(rsp, seq, [0x00, 0x02])];
            case OppoCommand.QueryVid:
                return [new OppoFrame(rsp, seq, [0x00, 0x9A, 0x07])];
            case OppoCommand.QueryMtu:
                return [new OppoFrame(rsp, seq, [0x00, 0x00, 0x02])];
            case OppoCommand.QueryCodec:
                return [new OppoFrame(rsp, seq, CodecResponse)];
            case OppoCommand.QueryUpgradeCapability:
                return [new OppoFrame(rsp, seq, [0x00, 0x00])];
            case OppoCommand.QueryEventCodes:
                return [new OppoFrame(rsp, seq, [0x00, 0x09, 0x01, 0x02, 0x03, 0x04, 0x08, 0x0B, 0xF1, 0xF2, 0xF3])];
            case OppoCommand.SubscribeEvents:
                return [new OppoFrame(rsp, seq, [0x00, p[0], .. p[1..].SelectMany(c => new byte[] { c, 0x00 })])];
            case OppoCommand.QueryFirmware:
                return [new OppoFrame(rsp, seq, [0x00, 0x03, .. "1,2,116,2,2,116,3,2,100"u8.ToArray()])];
            case OppoCommand.QueryBattery:
                return [new OppoFrame(rsp, seq, [0x00, (byte)(Battery.Length / 2), .. Battery])];
            case OppoCommand.QueryWearState:
                return [new OppoFrame(rsp, seq, [0x00, 0x03, 0x01, Wear[0], 0x02, Wear[1], 0x03, Wear[2]])];
            case OppoCommand.QueryNoiseControl:
                return p.Length >= 2 && p[0] == 0x02
                    ? [new OppoFrame(rsp, seq, [0x00, 0x02, p[1], .. MaskBytes(p[1] == 0x03 ? CycleLeft : CycleRight)])]
                    : p.Length >= 2 && p[0] == 0x03 ? [new OppoFrame(rsp, seq, [0x00, 0x03, 0x01, 0x00, 0x00])]
                    : p.Length >= 2 && p[0] == 0x04 ? [new OppoFrame(rsp, seq, [0x00, 0x04, 0x01, 0x40, 0x00])]
                    : [new OppoFrame(rsp, seq, [0x00, 0x01, 0x01, .. Mask(NoiseIndex, minBytes: 2)])];
            case OppoCommand.QueryEqualizer:
                return [new OppoFrame(rsp, seq, [0x00, EqualizerPreset])];
            case OppoCommand.QueryFeatureStatus:
            {
                if (_pendingFeature is { } pending)
                {
                    if (pending.Remaining <= 0) { Features[pending.Id] = pending.Value; _pendingFeature = null; }
                    else _pendingFeature = pending with { Remaining = pending.Remaining - 1 };
                }
                var ids = p.AsSpan(1).ToArray();
                var pairs = ids.Where(Features.ContainsKey).SelectMany(id => new[] { id, Features[id] }).ToArray();
                return [new OppoFrame(rsp, seq, [0x00, (byte)(pairs.Length / 2), .. pairs])];
            }
            case OppoCommand.QuerySpatialType:
                return [new OppoFrame(rsp, seq, [0x00, SpatialType])];
            case OppoCommand.QueryGameSound:
                return [new OppoFrame(rsp, seq, [0x00, GameSoundType, 0x02, 0x03, 0x00])];
            case OppoCommand.QueryPromptVolume:
                return [new OppoFrame(rsp, seq, [0x00, PromptVolume])];
            case OppoCommand.QueryBassEngine:
                return [new OppoFrame(rsp, seq, [0x00, 0xFD, 0x03, unchecked((byte)BassCurrent)])];
            case OppoCommand.QueryHeadMotionType:
                return [new OppoFrame(rsp, seq, [0x00, HeadMotionType])];
            case OppoCommand.QueryLeBrSwitch:
                return [new OppoFrame(rsp, seq, LeBrSwitchResponse ?? [0, (byte)(2 | (LeBrSwitchEnabled ? 1 : 0))])];
            case OppoCommand.SetLeBrSwitch:
                if (CanApply && p.Length == 1) LeBrSwitchEnabled = p[0] != 0;
                return [Ack(request)];
            case OppoCommand.QueryTouchControls:
                return [new OppoFrame(rsp, seq, [0x00, (byte)Keys.Count, .. Keys.SelectMany(k => new[] { k.Side, k.Button, k.Action, k.Function })])];
            case OppoCommand.QueryEqualizerList:
                return [new OppoFrame(rsp, seq, [0x00, .. EqListBody()])];
            case OppoCommand.QueryConnectedDevices:
                return [new OppoFrame(rsp, seq, [0x00, .. SourceListBody()])];
            case OppoCommand.QueryPriorityDevice:
                return PreferredWireAddress is { } wire
                    ? [new OppoFrame(rsp, seq, [0x00, 0x02, 0x01, .. wire])]
                    : [new OppoFrame(rsp, seq, [0x00, 0x02, 0x00])];

            case OppoCommand.SetNoiseControl:
            {
                if (DropSets) return null;
                if (!CanApply) return [Ack(request)];
                int index = LowestBit(p.AsSpan(2));
                if (p[0] == 0x02)
                {
                    uint mask = 0;
                    for (int i = 0; i < p.Length - 2; i++) mask |= (uint)p[2 + i] << (8 * i);
                    if (p[1] == 0x03) CycleLeft = mask; else CycleRight = mask;
                    return [Ack(request)];
                }
                NoiseIndex = ReportedIndexForSet(index);
                return [Ack(request), new OppoFrame(OppoCommand.DeviceEvent, 0x40, [0x03, 0x01, 0x01, .. Mask(NoiseIndex, 2)])];
            }
            case OppoCommand.SetEqualizer:
                if (DropSets) return null;
                if (CanApply) EqualizerPreset = p[0];
                return [Ack(request)];
            case OppoCommand.SetFeature:
                if (DropSets) return null;
                if (CanApply && FeatureSettleQueries > 0) _pendingFeature = (p[0], p[1], FeatureSettleQueries);
                else if (CanApply) Features[p[0]] = p[1];
                return [Ack(request)];
            case OppoCommand.SetSpatialAudio:
                if (DropSets) return null;
                if (CanApply) SpatialType = p[0];
                return [Ack(request)];
            case OppoCommand.SetGameSound:
                if (CanApply) GameSoundType = p[0];
                return [Ack(request)];
            case OppoCommand.SetPromptVolume:
                if (CanApply) PromptVolume = p[0];
                return [Ack(request)];
            case OppoCommand.SetBassEngine:
                if (CanApply) BassCurrent = (sbyte)p[2];
                return [Ack(request)];
            case OppoCommand.SetHeadMotionType:
                if (CanApply) HeadMotionType = p[0];
                return [Ack(request)];
            case OppoCommand.SetKeyFunction:
                if (CanApply)
                {
                    // devType 4 applies to both earbuds' records.
                    for (int i = 0; i < Keys.Count; i++)
                    {
                        var k = Keys[i];
                        if ((k.Side == p[1] || p[1] == 4) && k.Button == p[2] && k.Action == p[3]) Keys[i] = (k.Side, p[2], p[3], p[4]);
                    }
                }
                return [Ack(request)];
            case OppoCommand.FindEarbuds:
                return [Ack(request)];
            case OppoCommand.FitTest:
                if (p[0] == 0x01 && SetStatus == 0 && FitResult is { } fit)
                {
                    return [Ack(request), new OppoFrame(OppoCommand.DeviceEvent, 0x41, [0x04, 0x01, fit.Left, 0x02, fit.Right])];
                }
                return [Ack(request)];
            case OppoCommand.OperateMultiConnect:
            {
                if (!CanApply) return [Ack(request)];
                if (p[0] == 0x04)
                {
                    PreferredWireAddress = p[1] == 0x01 ? p.AsSpan(2, 6).ToArray().Reverse().ToArray() : null;
                    return [Ack(request)];
                }
                var wireAddress = p.AsSpan(1, 6).ToArray().Reverse().ToArray();
                int i = Sources.FindIndex(s => s.WireAddress.SequenceEqual(wireAddress));
                if (i >= 0)
                {
                    if (p[0] == 0x03) Sources.RemoveAt(i);
                    else Sources[i] = Sources[i] with { State = p[0] == 0x01 ? (byte)2 : (byte)3 };
                }
                return [Ack(request), new OppoFrame(OppoCommand.DeviceEvent, 0x42, [0x06, .. SourceListBody()])];
            }
            case OppoCommand.SetCustomEqualizer:
            {
                if (!CanApply) return [Ack(request)];
                byte action = p[0];
                int id = p[3];
                int nameLength = p[4];
                string name = System.Text.Encoding.UTF8.GetString(p, 5, nameLength);
                int bandCount = p[5 + nameLength];
                var gains = Enumerable.Range(0, bandCount).Select(b => (sbyte)p[6 + nameLength + b * 3 + 2]).ToArray();
                if (action == 0x01)
                {
                    int newId = 4 + CustomEq.Count;
                    for (int i = 0; i < CustomEq.Count; i++) CustomEq[i] = CustomEq[i] with { Selected = false };
                    CustomEq.Add((newId, name, true, gains));
                }
                else
                {
                    int i = CustomEq.FindIndex(c => c.Id == id);
                    if (i >= 0 && action == 0x03) CustomEq.RemoveAt(i);
                    else if (i >= 0)
                    {
                        for (int j = 0; j < CustomEq.Count; j++) CustomEq[j] = CustomEq[j] with { Selected = false };
                        CustomEq[i] = (id, name, true, gains);
                        EqualizerPreset = (byte)id;
                    }
                }
                return [new OppoFrame(rsp, seq, [0x00, (byte)id])];
            }
            case OppoCommand.SetHostCapabilities:
                return [Ack(request)];
            default:
                return [new OppoFrame(rsp, seq, [0x01])];
        }
    }

    private byte[] EqListBody()
    {
        var bytes = new List<byte> { (byte)CustomEq.Count };
        foreach (var (id, name, selected, gains) in CustomEq)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            bytes.AddRange([selected ? (byte)1 : (byte)0, 0xFA, 0x06, (byte)id, (byte)nameBytes.Length]);
            bytes.AddRange(nameBytes);
            bytes.Add((byte)EqBands.Length);
            for (int b = 0; b < EqBands.Length; b++)
            {
                bytes.AddRange([(byte)(EqBands[b] & 0xFF), (byte)(EqBands[b] >> 8), unchecked((byte)(b < gains.Length ? gains[b] : (sbyte)0))]);
            }
        }
        return [.. bytes];
    }

    private byte[] SourceListBody()
    {
        var bytes = new List<byte> { (byte)Sources.Count };
        foreach (var (wire, state, flags, name) in Sources)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            bytes.AddRange(wire);
            bytes.Add((byte)(3 + nameBytes.Length));
            bytes.AddRange([state, flags, (byte)nameBytes.Length]);
            bytes.AddRange(nameBytes);
        }
        return [.. bytes];
    }

    public static byte[] Mask(int index, int minBytes = 1)
    {
        var mask = new byte[Math.Max(minBytes, index / 8 + 1)];
        mask[index / 8] = (byte)(1 << (index % 8));
        return mask;
    }

    private static byte[] MaskBytes(uint mask) => mask <= 0xFF ? [(byte)mask] : [(byte)mask, (byte)(mask >> 8)];

    private static int LowestBit(ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < mask.Length * 8; i++)
        {
            if ((mask[i / 8] & (1 << (i % 8))) != 0) return i;
        }
        return -1;
    }
}
