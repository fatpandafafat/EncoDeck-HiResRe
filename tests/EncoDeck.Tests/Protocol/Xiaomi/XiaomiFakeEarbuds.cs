// SPDX-License-Identifier: GPL-3.0-only
// Default values are real-device captures published by community projects:
// - REDMI Buds 6 Pro session log attached to BudsLink PR #110 (https://github.com/maniacx/BudsLink/pull/110,
//   GPL-3.0, tester digoxi): device info, gesture, noise, EQ, spatial and custom-curve answers.
//   Only the in-ear switch record of run info is simulated; records carrying paired-device addresses
//   and the serial-number answer are left out.
// - REDMI Buds 8 Pro captures in robin-liquidium/redmi-buds-bar PROTOCOL.md (GPL-3.0, e4337d2).
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Xiaomi;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Xiaomi;

/// <summary>Simulated Xiaomi earbuds speaking RCSP, including the mutual auth round.</summary>
internal sealed class XiaomiFakeEarbuds : IDuplexByteChannel
{
    /// <summary>Device-info answer of a REDMI Buds 6 Pro (BudsLink PR #110): name, firmware 1.3.9.9, VID/PID 2717/509E, battery 95 / 100 / case n/a.</summary>
    public static readonly byte[] SixProTargetInfo = Hex(
        "1100 526564 6D69 2042 7564 7320 3620 5072 6F", "0501 13991399", "020264", "0503 2717509E", "020401", "020500",
        "0306 1399", "020801", "0407 5F64FF", "020D05", "020E03", "020F01");

    /// <summary>Custom curve answer of the same REDMI Buds 6 Pro: ±6 dB, ten flat bands 62 Hz – 16 kHz.</summary>
    public static readonly byte[] SixProCustomCurve = Hex(
        "01 00 06 06 00 00 0A", "003E00 007D00 00FA00 01F400 03E800 07D000 0FA000 1F4000 2EE000 3E8000");

    public static byte[] Hex(params string[] parts) => Convert.FromHexString(string.Concat(parts).Replace(" ", ""));

    private readonly FakeByteChannel _channel = new();
    private readonly byte[] _deviceRandom = Enumerable.Range(0x40, 16).Select(i => (byte)i).ToArray();
    private byte _deviceSequence;

    public string Description => "simulated Xiaomi earbuds";

    /// <summary>Config values, initialised from the REDMI Buds 6 Pro capture.</summary>
    public Dictionary<ushort, byte[]> Values { get; } = new()
    {
        [XiaomiConfig.Gestures] = Convert.FromHexString("040808010101020303030606050B0B"),
        [XiaomiConfig.AutoAnswer] = [0],
        [XiaomiConfig.DualConnection] = [1],
        [XiaomiConfig.EqualizerPreset] = [0],
        [XiaomiConfig.Noise] = [1, 0],
        [XiaomiConfig.WearState] = [0],
        [XiaomiConfig.Spatial] = [0],
        [XiaomiConfig.AdaptiveAnc] = [0],
        [XiaomiConfig.LowLatency] = [0],
        [XiaomiConfig.CustomEqualizer] = SixProCustomCurve,
        [XiaomiConfig.PromptVolume] = [5, 5, 10, 1],
    };

    /// <summary>Config ids that never get an answer (some firmware stalls on unknown ids).</summary>
    public HashSet<ushort> Silent { get; } = [];

    public byte[] TargetInfo { get; set; } = SixProTargetInfo;

    /// <summary>Run-info record 10 (in-ear detection switch, 0 = on); null when the record is missing.</summary>
    public byte? WearDetection { get; set; } = 0;

    public bool AnswersAuth { get; set; } = true;

    /// <summary>Whether the earbuds run their own challenge round after ours.</summary>
    public bool ChallengesBack { get; set; } = true;

    /// <summary>Whether our answer to the earbuds' challenge was correct (null until answered).</summary>
    public bool? PeerVerified { get; private set; }

    public bool AcceptWrites { get; set; } = true;

    public bool AckWrites { get; set; } = true;

    /// <summary>Status of write acknowledgements (4 = unsupported).</summary>
    public byte WriteStatus { get; set; }

    public List<XiaomiFrame> Received { get; } = [];

    public List<byte[]> FindWrites { get; } = [];

    public static (IDeviceController Controller, DeviceStateStore Store) Connect(XiaomiFakeEarbuds earbuds, string name = "Redmi Buds 6 Pro")
    {
        var identity = new DeviceIdentity("xiaomi-test", name, new BluetoothAddress(0x112233445566));
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = identity, Profile = XiaomiDeviceProfile.Redmi });
        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        return (XiaomiDeviceProfile.Redmi.CreateController(earbuds, context), store);
    }

    public IReadOnlyList<XiaomiFrame> Snapshot()
    {
        lock (Received) return [.. Received];
    }

    /// <summary>Pushes a config change the way the earbuds do (kind 0xC4 asks for an acknowledgement).</summary>
    public void Notify(byte[] payload, byte kind = 0xC4) => _channel.Receive(new XiaomiFrame(kind, XiaomiProtocol.OpNotifyConfig, 99, payload).Encode());

    public void Push(byte[] frame) => _channel.Receive(frame);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _channel.WriteAsync(data, cancellationToken);
        foreach (var f in new XiaomiFrameDecoder().Feed(data.Span))
        {
            lock (Received) Received.Add(f);
            if (f.IsResponse) HandleHostResponse(f);
            else HandleHostRequest(f);
        }
    }

    private void Respond(XiaomiFrame request, byte[] data, byte status = 0) =>
        _channel.Receive(XiaomiFrame.Response(XiaomiProtocol.KindResponse, request.OpCode, request.Sequence, data, status).Encode());

    private void SendRequest(byte opCode, byte[] payload) =>
        _channel.Receive(new XiaomiFrame(0xC0, opCode, _deviceSequence++, payload).Encode());

    private void HandleHostResponse(XiaomiFrame raw)
    {
        var f = XiaomiConfigCodec.NormalizeResponse(raw)!;
        if (f.OpCode != XiaomiProtocol.OpAuthCheck) return;
        PeerVerified = f.Payload.Length == 17 && f.Payload[0] == 1
            && f.Payload.AsSpan(1).SequenceEqual(XiaomiAuth.GetEncryptedAuthCheckData(_deviceRandom));
        SendRequest(XiaomiProtocol.OpAuthResult, [1, PeerVerified == true ? (byte)0 : (byte)1]);
    }

    private void HandleHostRequest(XiaomiFrame f)
    {
        switch (f.OpCode)
        {
            case XiaomiProtocol.OpAuthCheck when AnswersAuth:
                Respond(f, [1, .. XiaomiAuth.GetEncryptedAuthCheckData(f.Payload.AsSpan(1, 16))]);
                break;
            case XiaomiProtocol.OpAuthResult when AnswersAuth:
                Respond(f, [1]);
                if (ChallengesBack) SendRequest(XiaomiProtocol.OpAuthCheck, [1, .. _deviceRandom]);
                break;
            case XiaomiProtocol.OpTargetInfo:
                Respond(f, TargetInfo);
                break;
            case XiaomiProtocol.OpRunInfo:
                if (WearDetection is { } wear) Respond(f, [2, XiaomiProtocol.RunInfoWearDetection, wear]);
                else Respond(f, [], XiaomiProtocol.StatusUnsupported);
                break;
            case XiaomiProtocol.OpSetDeviceInfo when f.Payload is [2, XiaomiProtocol.DeviceInfoWearDetection, var value]:
                if (AcceptWrites) WearDetection = value;
                if (AckWrites) Respond(f, [], WriteStatus);
                break;
            case XiaomiProtocol.OpGetConfig:
                Assert.Equal(2, f.Payload.Length); // one id per read
                ushort id = (ushort)((f.Payload[0] << 8) | f.Payload[1]);
                if (Silent.Contains(id)) break;
                if (Values.TryGetValue(id, out var current)) Respond(f, XiaomiConfigCodec.WritePayload(id, current));
                else Respond(f, [], XiaomiProtocol.StatusUnsupported);
                break;
            case XiaomiProtocol.OpSetConfig when XiaomiConfigCodec.TryParse(f.Payload, out var configs):
                foreach (var (configId, value) in configs)
                {
                    if (configId == XiaomiConfig.FindEarbuds) FindWrites.Add(value);
                    else if (AcceptWrites) Store(configId, value);
                }
                if (AckWrites) Respond(f, [], WriteStatus);
                break;
        }
    }

    private void Store(ushort id, byte[] value)
    {
        switch (id)
        {
            case XiaomiConfig.Gestures:
                var groups = Values[id].ToArray();
                for (int i = 0; i < groups.Length; i += 3)
                {
                    if (groups[i] != value[0]) continue;
                    if (value[1] != 0xFF) groups[i + 1] = value[1];
                    if (value[2] != 0xFF) groups[i + 2] = value[2];
                }
                Values[id] = groups;
                break;
            case XiaomiConfig.PromptVolume:
                Values[id][0] = value[0];
                break;
            case XiaomiConfig.CustomEqualizer:
                // Saved as [1,10,1,1,1,0,count,bands]; read back with the device's own header.
                Values[id] = [.. Values[id].AsSpan(0, 6), .. value.AsSpan(6)];
                break;
            default:
                Values[id] = value;
                break;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _channel.ReadAsync(buffer, cancellationToken);

    public ValueTask DisposeAsync() => _channel.DisposeAsync();
}
