// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Oppo.Models;
using EncoDeck.Tests.Fakes;

namespace EncoDeck.Tests.Protocol;

public class OppoAir5sCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IAsyncDisposable
    {
        public FakeOppoDevice Device { get; } = new();
        public DeviceStateStore Store { get; } = new();
        public OppoSession Session { get; }
        public OppoDeviceController Controller { get; }
        private bool _controllerDisposed;

        public Harness(uint productId = 0x06C810, bool leAudio = false)
        {
            Device.ProductId = productId;
            var identity = new DeviceIdentity("codec-test", "Renamed earbuds", new BluetoothAddress(0x112233445566));
            Store.Reset(DeviceState.Empty with { Device = identity });
            Session = new OppoSession(Device.Channel);
            Session.Start();
            // Begin with a generic name match: the device's product id must select the rule.
            Controller = new OppoDeviceController(Session, new OppoDeviceProfile(OppoModels.GenericOppo), identity, Store)
            {
                IsLeAudioTransport = leAudio,
                CodecPollInterval = TimeSpan.FromMilliseconds(50),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await StopControllerAsync();
            await Session.DisposeAsync();
        }

        public async Task StopControllerAsync()
        {
            if (_controllerDisposed) return;
            _controllerDisposed = true;
            await Controller.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(0x06C810u, 1, AudioCodec.Sbc)]
    [InlineData(0x06C810u, 2, AudioCodec.Aac)]
    [InlineData(0x06E810u, 1, AudioCodec.Sbc)]
    [InlineData(0x06E810u, 2, AudioCodec.Aac)]
    [InlineData(0x06EC10u, 7, AudioCodec.Lhdc)]
    [InlineData(0x06EC10u, 8, AudioCodec.Lhdc)]
    public async Task Report_UsesActualProductId_AndKeepsOtherModels(uint productId, byte id, AudioCodec expected)
    {
        await using var h = new Harness(productId);
        h.Device.CodecResponse = [0, id];
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Equal(expected, h.Store.Current.Codec?.Value);
        Assert.Equal(id, h.Store.Current.ReportedCodecId?.Value);
    }

    [Theory]
    [InlineData(0x06C810u, 3)]
    [InlineData(0x06C810u, 4)]
    [InlineData(0x06C810u, 5)]
    [InlineData(0x06C810u, 6)]
    [InlineData(0x06C810u, 7)]
    [InlineData(0x06C810u, 8)]
    [InlineData(0x06C810u, 9)]
    [InlineData(0x06E810u, 7)]
    [InlineData(0x06E810u, 8)]
    public async Task Air5s_AnomalousReportClearsOldCodec_ButPreservesRawId(uint productId, byte id)
    {
        await using var h = new Harness(productId);
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Equal(AudioCodec.Aac, h.Store.Current.Codec?.Value);

        h.Device.CodecResponse = [0, id];
        await h.Controller.RefreshAsync(Ct);

        Assert.Null(h.Store.Current.Codec);
        Assert.Equal(id, h.Store.Current.ReportedCodecId?.Value);
    }

    [Fact]
    public async Task Air5s_PollsCodecChanges_AndRecoversFromAnomalousReport()
    {
        await using var h = new Harness();
        h.Device.CodecResponse = [0, 7];
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Null(h.Store.Current.Codec);

        h.Device.CodecResponse = [0, 2];
        await OppoDeviceControllerTests.WaitUntil(() => h.Store.Current.Codec?.Value == AudioCodec.Aac);
        h.Device.CodecResponse = [0, 1];
        await OppoDeviceControllerTests.WaitUntil(() => h.Store.Current.Codec?.Value == AudioCodec.Sbc);
        h.Device.CodecResponse = [1, 1];
        await OppoDeviceControllerTests.WaitUntil(() => h.Store.Current.Codec is null && h.Store.Current.ReportedCodecId is null);
    }

    [Theory]
    [InlineData(0x06EC10u, false, true)] // Another model: preserve its current polling behavior.
    [InlineData(0x06C810u, true, true)]  // LE transport: no A2DP polling.
    [InlineData(0x06C810u, false, false)] // Firmware does not advertise the read.
    public async Task CodecPoll_IsLimitedToAir5sClassicWithCapability(uint productId, bool leAudio, bool supportsCodec)
    {
        await using var h = new Harness(productId, leAudio);
        if (!supportsCodec) h.Device.Capabilities[2] &= 0xFD; // zero-based bit 17
        Assert.True(await h.Controller.InitializeAsync(Ct));
        int before;
        lock (h.Device.Received) before = h.Device.Received.Count(f => f.Command == OppoCommand.QueryCodec);
        await Task.Delay(180, Ct);
        lock (h.Device.Received) Assert.Equal(before, h.Device.Received.Count(f => f.Command == OppoCommand.QueryCodec));
    }

    [Fact]
    public async Task NewSession_WithoutCodecCapability_DoesNotRevivePreviousReport()
    {
        await using var h = new Harness();
        h.Store.TryUpdate(null, s => s with
        {
            Codec = Observed.From(AudioCodec.Aac, DateTimeOffset.UtcNow, StateSource.QueryResponse),
            ReportedCodecId = Observed.From((byte)2, DateTimeOffset.UtcNow, StateSource.QueryResponse),
        });
        h.Device.Capabilities[2] &= 0xFD;
        Assert.True(await h.Controller.InitializeAsync(Ct));
        Assert.Null(h.Store.Current.Codec);
        Assert.Null(h.Store.Current.ReportedCodecId);
    }

    [Fact]
    public async Task CodecPoll_DisposeStopsFurtherReads()
    {
        await using var h = new Harness();
        Assert.True(await h.Controller.InitializeAsync(Ct));
        h.Device.CodecResponse = [0, 1];
        await OppoDeviceControllerTests.WaitUntil(() => h.Store.Current.Codec?.Value == AudioCodec.Sbc);
        await h.StopControllerAsync();
        int before;
        lock (h.Device.Received) before = h.Device.Received.Count;
        await Task.Delay(180, Ct);
        lock (h.Device.Received) Assert.Equal(before, h.Device.Received.Count);
    }
}
