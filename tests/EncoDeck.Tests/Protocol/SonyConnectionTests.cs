// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Devices;
using EncoDeck.Core.Privacy;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Protocol;

public sealed class SonyConnectionTests
{
    internal static readonly DeviceIdentity Identity = new("sony-xm6", "WH-1000XM6", new BluetoothAddress(1));
    internal static IControllableProfile Profile => (IControllableProfile)SonyAkgProfiles.CreateBuiltIn().Single(p => p.DisplayName == Identity.Name);

    // A peer that gates its protocol response on the host ACKing a different data table.
    // This reproduces a device send queue blocked by an unhandled common/table-2 frame.
    internal sealed class Peer : IDuplexByteChannel
    {
        private readonly FakeByteChannel _channel = new();
        private byte _sequence;
        private byte? _waitingForAck;
        private byte[]? _deferred;
        public bool DropFirstAck { get; init; }
        public bool CloseOnWrite { get; init; }
        public byte? PreludeType { get; init; }
        public List<SonyFrame> Requests { get; } = [];
        public List<SonyFrame> Acknowledgements { get; } = [];
        public bool IsDisposed => _channel.IsDisposed;
        public string Description => "Sony test peer";

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            var frame = Assert.Single(new SonyFrameCodec().Feed(data.Span));
            if (frame.Type == 1)
            {
                Acknowledgements.Add(frame);
                if (frame.Sequence == _waitingForAck)
                {
                    _waitingForAck = null;
                    if (_deferred is { } response) { _deferred = null; Reply(response); }
                }
                return ValueTask.CompletedTask;
            }
            Requests.Add(frame);
            if (CloseOnWrite) { _channel.RemoteClose(); return ValueTask.CompletedTask; }
            if (DropFirstAck && Requests.Count == 1)
            {
                // Wrong sequence ACK must not advance the host send bit.
                _channel.Receive(SonyFrameCodec.Encode(1, frame.Sequence, []));
                return ValueTask.CompletedTask;
            }
            _channel.Receive(SonyFrameCodec.Encode(1, (byte)(frame.Sequence ^ 1), []));
            byte[] payload = frame.Payload[0] switch
            {
                0 => [1, 0, 0, 0, 2, 0, 0, 0],
                4 => [5, 2, 5, (byte)'3', (byte)'.', (byte)'1', (byte)'.', (byte)'5'],
                6 => [7, 0, 2, 32, 1, 107, 1],
                0x22 => [0x23, 0, 80, 0],
                0x66 => [0x67, 23, 1, 1, 1, 0, 20],
                _ => throw new InvalidOperationException("Unexpected command during read-only startup"),
            };
            if (frame.Payload[0] == 0 && PreludeType is { } type)
            {
                _deferred = payload;
                _waitingForAck = (byte)(_sequence ^ 1);
                byte[] prelude = SonyFrameCodec.Encode(type, _sequence, [0x23, 0, 1, 0]);
                _sequence ^= 1;
                _channel.Receive(prelude);
                _channel.Receive(prelude); // Duplicates must still be ACKed.
            }
            else Reply(payload);
            return ValueTask.CompletedTask;
        }

        private void Reply(byte[] payload)
        {
            _channel.Receive(SonyFrameCodec.Encode(12, _sequence, payload));
            _sequence ^= 1;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _channel.ReadAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync() => _channel.DisposeAsync();
    }

    [Theory]
    [InlineData((byte)13)] // DATA_COMMON
    [InlineData((byte)14)] // DATA_MDR_NO2
    [InlineData((byte)45)] // LARGE_DATA_COMMON
    public async Task Xm6_InitializesWhenOtherTablesBlockThePeerQueue(byte type)
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = Identity, Profile = Profile });
        var peer = new Peer { PreludeType = type };
        await using var controller = Profile.CreateController(peer, new(Identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(80, store.Current.Battery.Left?.Value.Level);
        Assert.Equal(NoiseControlMode.Transparency, store.Current.NoiseControl?.Value.Mode);
        Assert.True(peer.Acknowledgements.Count >= 6);
        Assert.All(peer.Requests, frame => Assert.Contains(frame.Payload[0], new byte[] { 0, 4, 6, 0x22, 0x66 }));
    }

    [Fact]
    public async Task LostAck_RetransmitsIdenticalFrameThenAdvancesSequence()
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = Identity, Profile = Profile });
        var peer = new Peer { DropFirstAck = true };
        await using var controller = Profile.CreateController(peer, new(Identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.True(await controller.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(peer.Requests[0].Payload, peer.Requests[1].Payload);
        Assert.Equal(peer.Requests[0].Sequence, peer.Requests[1].Sequence);
        Assert.Equal(1, peer.Requests[2].Sequence);
    }

    [Fact]
    public async Task ImmediateDisconnect_EndsHandshakeWithoutWaitingForAckRetries()
    {
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = Identity, Profile = Profile });
        var peer = new Peer { CloseOnWrite = true };
        await using var controller = Profile.CreateController(peer, new(Identity, store, () => false, NullLoggerFactory.Instance, TimeProvider.System));
        Assert.False(await controller.InitializeAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Single(peer.Requests);
        Assert.Null(store.Current.Battery.Left);
    }
}
