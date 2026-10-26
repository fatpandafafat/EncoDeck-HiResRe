// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Bluetooth.Control;
using EncoDeck.Bluetooth.Rfcomm;
using EncoDeck.Core.Connection;
using EncoDeck.Core.Devices;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using EncoDeck.Tests.Protocol;
using Windows.Networking.Sockets;

namespace EncoDeck.Tests.Bluetooth;

public sealed class SonyControlConnectionTests
{
    private sealed class Link : ILinkStateSource
    {
        public bool IsConnected => true;
        public event EventHandler<bool>? ConnectionChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class Connector : IChannelConnector
    {
        public List<Guid> Services { get; } = [];
        public List<SonyConnectionTests.Peer> Peers { get; } = [];
        public Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken)
        {
            var service = serviceUuids[0];
            Services.Add(service);
            var peer = new SonyConnectionTests.Peer { CloseOnWrite = service == SonyAkgProfiles.PrimaryServiceUuid };
            Peers.Add(peer);
            return Task.FromResult(new ChannelOpenResult(ChannelOpenStatus.Opened, peer, service, null));
        }
    }

    [Fact]
    public async Task FailedPrimaryHandshake_UsesLegacyServiceAndKeepsItOnReconnect()
    {
        var connector = new Connector();
        var store = new DeviceStateStore();
        store.Reset(DeviceState.Empty with { Device = SonyConnectionTests.Identity, Profile = SonyConnectionTests.Profile });
        await using var supervisor = new ControlChannelSupervisor(SonyConnectionTests.Identity, SonyConnectionTests.Profile,
            new Link(), connector, store, new ReconnectPolicy(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromMilliseconds(40), 3),
            linkSettleDelay: TimeSpan.Zero);
        supervisor.Start();
        await OppoDeviceControllerTests.WaitUntil(() => store.Current.Connection == DeviceConnectionState.ControlReady);
        Assert.Equal(new[] { SonyAkgProfiles.PrimaryServiceUuid, SonyAkgProfiles.LegacyServiceUuid }, connector.Services);
        Assert.True(connector.Peers[0].IsDisposed);
        Assert.Equal(80, store.Current.Battery.Left?.Value.Level);
        supervisor.RequestReconnect();
        await OppoDeviceControllerTests.WaitUntil(() => connector.Services.Count == 3 && store.Current.Connection == DeviceConnectionState.ControlReady);
        Assert.Equal(SonyAkgProfiles.LegacyServiceUuid, connector.Services[2]);
        Assert.True(connector.Peers[1].IsDisposed);
        Assert.Null(store.Current.LastError);
    }

    [Fact]
    public void SonyUsesSecureRfcommLikeOfficialApp()
    {
        Assert.Equal(SocketProtectionLevel.BluetoothEncryptionWithAuthentication, RfcommChannelConnector.ProtectionLevelFor(SonyAkgProfiles.PrimaryServiceUuid));
        Assert.Equal(SocketProtectionLevel.BluetoothEncryptionWithAuthentication, RfcommChannelConnector.ProtectionLevelFor(SonyAkgProfiles.LegacyServiceUuid));
        Assert.Equal(SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication, RfcommChannelConnector.ProtectionLevelFor(new Guid("00001101-0000-1000-8000-00805f9b34fb")));
    }

    [Theory]
    [InlineData(@"Bluetooth#Bluetooth00:11:22:33:44:55-66:77:88:99:aa:bb#RFCOMM:00000009:{956c7b26-d49a-4ba8-b03f-b17d393cb6e2}", 9)]
    [InlineData("RFCOMM:0000001E:{uuid}", 30)]
    // An SDP record without a protocol descriptor: Windows would block for the whole connect timeout.
    [InlineData("Bluetooth#Bluetooth00:11:22:33:44:55-66:77:88:99:aa:bb#RFCOMM:00000000:{956c7b26-d49a-4ba8-b03f-b17d393cb6e2}", 0)]
    [InlineData("RFCOMM:0000001F:{uuid}", 0)]
    [InlineData("RFCOMM::{uuid}", 0)]
    [InlineData("no channel here", 0)]
    [InlineData(null, 0)]
    public void ResolvesTheRfcommChannelFromTheServiceName(string? serviceName, int channel) =>
        Assert.Equal(channel, RfcommChannelConnector.ResolveRfcommChannel(serviceName));

    [Fact]
    public void KnownSonyChannelIsOnlyForTheVerifiedModel()
    {
        Assert.Equal(RfcommChannelConnector.SonyMdrV2Channel, RfcommChannelConnector.KnownSonyControlChannel("WH-1000XM6"));
        Assert.Null(RfcommChannelConnector.KnownSonyControlChannel("WH-1000XM5"));
        Assert.Null(RfcommChannelConnector.KnownSonyControlChannel(null));
    }
}
