// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol.Edifier;

public sealed class EdifierProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceIdentity Device(string name) => new("dev-edifier", name, new BluetoothAddress(0x0011223344AA));

    [Fact]
    public void Encode_BatteryQuery_MatchesDocumentedChecksum()
    {
        // 8217 (0x2019) + 0xAA + 0x01 + 0xD0 = 0x2194
        Assert.Equal(new byte[] { 0xAA, 0x01, 0xD0, 0x21, 0x94 }, EdifierFrameCodec.Encode(0xD0, []));
    }

    [Fact]
    public void Encode_KeepsLastPayloadByte()
    {
        var frame = EdifierFrameCodec.Encode(0xC1, [0x03]);
        Assert.Equal(6, frame.Length);
        Assert.Equal(new byte[] { 0xAA, 0x02, 0xC1, 0x03 }, frame[..4]);
    }

    [Fact]
    public void Decoder_RoundTripsAcrossPartialReadsAndGarbage()
    {
        var a = EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xD0, [77]);
        var b = EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadCc, 0xCC, [2, 5]);
        byte[] stream = [0x00, 0x13, .. a, .. b];
        var decoder = new EdifierFrameDecoder();
        var frames = new List<EdifierFrame>();
        foreach (byte x in stream) frames.AddRange(decoder.Feed([x]));

        Assert.Equal(2, frames.Count);
        Assert.Equal(0xD0, frames[0].Command);
        Assert.Equal(new byte[] { 77 }, frames[0].Payload);
        Assert.Equal(0xCC, frames[1].Command);
        Assert.Equal(new byte[] { 2, 5 }, frames[1].Payload);
    }

    [Fact]
    public void Decoder_SkipsBadChecksum()
    {
        var bad = EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xD0, [50]);
        bad[^1] ^= 0xFF;
        var good = EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xD0, [60]);
        var frames = new EdifierFrameDecoder().Feed([.. bad, .. good]);
        Assert.Equal(new byte[] { 60 }, Assert.Single(frames).Payload);
    }

    [Theory]
    [InlineData("W820NB", "edifier-w820nb")]
    [InlineData("EDIFIER W820NB", "edifier-w820nb")]
    [InlineData("W820NB Plus", "edifier-w820nb")]
    [InlineData("EDIFIER W200BT Plus", "edifier-w200bt-plus")]
    [InlineData("EDIFIER W200BT", "edifier-w200bt")]
    [InlineData("EDIFIER NeoBuds", "edifier")]
    public void Registry_MatchesByName(string name, string modelId)
    {
        var registry = new DeviceProfileRegistry(EdifierProfiles.CreateBuiltIn());
        Assert.Equal(modelId, registry.Match(Device(name))?.ModelId);
    }

    [Fact]
    public void Profiles_ScoreExactNameHighestAndIgnoreOthers()
    {
        var profiles = EdifierProfiles.CreateBuiltIn();
        var w820 = profiles.Single(p => p.ModelId == "edifier-w820nb");
        Assert.Equal(100, w820.MatchScore(Device("W820NB")));
        Assert.Equal(0, w820.MatchScore(Device("OPPO Enco X4")));
        Assert.All(profiles, p => Assert.Equal(0, p.MatchScore(Device("OPPO Enco X4"))));
        Assert.True(((IControllableProfile)profiles.Single(p => p.ModelId == "edifier")).IsGeneric);
    }

    [Fact]
    public async Task Controller_StartsReadsBatteryAndConfirmsNoiseWrite()
    {
        var store = new DeviceStateStore();
        var identity = Device("W820NB");
        var profile = (IControllableProfile)EdifierProfiles.CreateBuiltIn()[0];
        store.Reset(DeviceState.Empty with { Device = identity, Profile = profile });
        var channel = new FakeByteChannel();
        using var deviceStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var device = Task.Run(() => EmulateAsync(channel, deviceStop.Token), Ct);

        var context = new ControllerContext(identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System);
        await using var controller = profile.CreateController(channel, context);

        Assert.True(await controller.InitializeAsync(Ct));
        Assert.Equal(80, store.Current.Battery.Left?.Value.Level);
        Assert.Null(store.Current.Battery.Right);
        Assert.Equal(NoiseControlMode.Off, store.Current.NoiseControl?.Value.Mode);

        var result = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.NoiseCancellation), Ct);
        Assert.Equal(CommandStatus.Success, result.Status);
        Assert.Equal(NoiseControlMode.NoiseCancellation, store.Current.NoiseControl?.Value.Mode);
        Assert.True(store.Current.NoiseControl!.IsConfirmed);
        Assert.Empty(store.Current.PendingCommands);
        Assert.Contains(channel.Written, w => w.SequenceEqual(EdifierFrameCodec.Encode(EdifierConstants.SetNoiseMode, [EdifierConstants.NoiseAnc])));

        var adaptive = await controller.SetNoiseControlAsync(new NoiseControlState(NoiseControlMode.Adaptive), Ct);
        Assert.Equal(CommandStatus.Unsupported, adaptive.Status);

        await deviceStop.CancelAsync();
        await device;
    }

    /// <summary>Answers every written request like a W820NB at 80 % that starts with noise control off.</summary>
    private static async Task EmulateAsync(FakeByteChannel channel, CancellationToken ct)
    {
        byte mode = EdifierConstants.NoiseOff;
        int handled = 0;
        var decoder = new RequestReader();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var written = channel.Written;
                for (; handled < written.Count; handled++)
                {
                    var request = decoder.Parse(written[handled]);
                    byte[] reply = request.Command switch
                    {
                        EdifierConstants.QueryBattery => EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xD0, [80]),
                        EdifierConstants.QueryNoiseMode => EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xCC, [mode, 0]),
                        EdifierConstants.SetNoiseMode => SetMode(request.Payload[0]),
                        EdifierConstants.QueryGameMode => EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0x08, [0]),
                        EdifierConstants.QueryPowerOffTimer => EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xD3, [0]),
                        _ => [],
                    };
                    if (reply.Length == 0) continue;
                    // Split each answer in two to exercise partial-frame buffering.
                    channel.Receive(reply[..2]);
                    channel.Receive(reply[2..]);
                }
                await Task.Delay(5, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }

        byte[] SetMode(byte value)
        {
            mode = value;
            return EdifierFrameCodec.Encode(EdifierConstants.ResponseHeadBb, 0xC1, []);
        }
    }

    private sealed class RequestReader
    {
        public EdifierFrame Parse(byte[] frame)
        {
            Assert.Equal(EdifierConstants.RequestHead, frame[0]);
            Assert.Equal(frame[1] + 4, frame.Length);
            ushort sum = EdifierConstants.ChecksumSeed;
            foreach (byte b in frame[..^2]) sum = unchecked((ushort)(sum + b));
            Assert.Equal((byte)(sum >> 8), frame[^2]);
            Assert.Equal((byte)sum, frame[^1]);
            return new EdifierFrame(frame[2], frame[3..^2]);
        }
    }
}
