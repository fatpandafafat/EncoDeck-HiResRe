// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Sockets;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Bluetooth.Rfcomm;

/// <summary>Windows AF_BTH stream for a model's known RFCOMM channel when SDP does not expose it.</summary>
internal sealed class BluetoothSocketByteChannel(Socket socket, uint channel, string label = "MOONDROP") : IDuplexByteChannel
{
    private int _disposed;

    public string Description => $"RFCOMM {label} channel {channel}";

    internal static async Task<ChannelOpenResult> OpenAsync(ulong address, uint channel, CancellationToken cancellationToken, string label = "MOONDROP")
    {
        if (channel is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(channel));
        cancellationToken.ThrowIfCancellationRequested();
        var socket = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var cancelConnect = timeout.Token.Register(socket.Dispose);
        try
        {
            // Socket.ConnectAsync uses ConnectEx, which the Windows Bluetooth provider rejects.
            // Ordinary Winsock connect runs off the UI thread; disposal cancels the blocking call.
            await Task.Run(() => socket.Connect(new BluetoothEndPoint(address, channel)), timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new ChannelOpenResult(ChannelOpenStatus.Opened, new BluetoothSocketByteChannel(socket, channel, label), MoondropConstants.SppServiceId, null);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            socket.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, timeout.IsCancellationRequested
                ? "RFCOMM connection timed out."
                : $"RFCOMM channel {channel}: {ex.Message} (0x{ex.HResult:X8})");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while (!data.IsEmpty)
        {
            int written = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (written == 0) throw new IOException("RFCOMM closed during write.");
            data = data[written..];
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) socket.Dispose();
        return ValueTask.CompletedTask;
    }

    // SOCKADDR_BTH is packed: AF_BTH(2), BTH_ADDR(8), service GUID(16), channel(4).
    // The service GUID is ignored when a nonzero RFCOMM channel is specified.
    private sealed class BluetoothEndPoint(ulong address, uint channel) : EndPoint
    {
        public override AddressFamily AddressFamily => (AddressFamily)32;

        public override SocketAddress Serialize()
        {
            var result = new SocketAddress(AddressFamily, 30);
            for (int i = 0; i < 8; i++) result[i + 2] = (byte)(address >> (8 * i));
            for (int i = 0; i < 4; i++) result[i + 26] = (byte)(channel >> (8 * i));
            return result;
        }

        public override EndPoint Create(SocketAddress socketAddress) => this;
    }
}
