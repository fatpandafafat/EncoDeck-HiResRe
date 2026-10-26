// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Sony;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// Sony's official MDR v2 control stream carried over the LE Audio GATT link.
/// A headset connected under the LE Audio identity exposes no Classic SDP record, so the
/// RFCOMM control channel of the same protocol is unreachable; the same frames travel
/// through this service instead. Notifications are a byte stream, not messages: frames may
/// be split or coalesced, so the channel concatenates them and lets SonyFrameCodec reassemble.
/// </summary>
internal sealed class SonyGattByteChannel : IDuplexByteChannel
{
    internal static readonly Guid ServiceUuid = SonyAkgProfiles.BleServiceUuid;
    internal static readonly Guid WriteCharacteristicUuid = SonyAkgProfiles.BleWriteUuid;
    internal static readonly Guid NotifyCharacteristicUuid = SonyAkgProfiles.BleNotifyUuid;

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _write;
    private readonly GattCharacteristic _notify;
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private byte[] _pending = [];
    private int _pendingOffset;
    private int _stopped;
    private int _disposed;

    private SonyGattByteChannel(BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic write, GattCharacteristic notify)
    {
        _device = device;
        _service = service;
        _write = write;
        _notify = notify;
        _shutdownToken = _shutdown.Token;
        // The headset drops an idle control link; the session has to stay alive for the
        // supervisor's reconnect loop and for unsolicited status frames to keep arriving.
        try { service.Session.MaintainConnection = true; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException) { }
        _notify.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Description => "Sony MDR v2 over LE Audio GATT";

    internal static async Task<ChannelOpenResult> OpenAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattCharacteristic? write = null;
        GattCharacteristic? notify = null;
        SonyGattByteChannel? channel = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // A Classic pairing leaves the LE object unfindable by address alone on some radios, so
            // the public-address endpoint is the fallback rather than the first step: discovery
            // costs several seconds the supervisor's connect deadline cannot spare.
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is null)
            {
                string? endpointId = await DiscoverEndpointAsync(bluetoothAddress, timeout.Token).ConfigureAwait(false);
                if (endpointId is not null)
                    device = await BluetoothLEDevice.FromIdAsync(endpointId).AsTask(timeout.Token).ConfigureAwait(false);
            }
            if (device is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound,
                    "The headset's Bluetooth LE control endpoint was not found.");

            GattDeviceServicesResult services = await device
                .GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(timeout.Token).ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success)
                return Failed(services.Status, "Sony LE Audio control service");
            if (services.Services.Count == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound,
                    "The headset does not expose Sony's LE Audio control service in its current Bluetooth mode.");

            service = services.Services[0];
            GattCharacteristicsResult characteristics = await service
                .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask(timeout.Token).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success)
                return Failed(characteristics.Status, "Sony LE Audio control characteristics");
            write = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == WriteCharacteristicUuid);
            notify = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == NotifyCharacteristicUuid);
            if (write is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound,
                    "The Sony LE Audio write characteristic is missing.");
            if (notify is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound,
                    "The Sony LE Audio notification characteristic is missing.");
            if ((write.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed,
                    "The Sony LE Audio write characteristic is not writable.");

            channel = new SonyGattByteChannel(device, service, write, notify);
            device = null;
            service = null;
            using var connected = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, channel._shutdownToken);
            var status = await notify
                .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(connected.Token).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success) return Failed(status, "Sony LE Audio notification subscription");
            connected.Token.ThrowIfCancellationRequested();
            var result = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, SonyGattByteChannel.ServiceUuid, null);
            channel = null;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "The Sony LE Audio control channel timed out.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"Sony LE Audio GATT: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            service?.Dispose();
            device?.Dispose();
        }
    }

    private static async Task<string?> DiscoverEndpointAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(bluetoothAddress, BluetoothAddressType.Public),
            [], DeviceInformationKind.AssociationEndpoint);
        void OnAdded(DeviceWatcher sender, DeviceInformation info) => found.TrySetResult(info.Id);
        void OnStopped(DeviceWatcher sender, object args) => found.TrySetResult(null);
        watcher.Added += OnAdded;
        watcher.Stopped += OnStopped;
        try
        {
            watcher.Start();
            return await found.Task.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            watcher.Added -= OnAdded;
            watcher.Stopped -= OnStopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
    }

    private static ChannelOpenResult Failed(GattCommunicationStatus status, string operation) =>
        ChannelOpenResult.Failed(status switch
        {
            GattCommunicationStatus.AccessDenied => ChannelOpenStatus.AccessDenied,
            GattCommunicationStatus.Unreachable => ChannelOpenStatus.DeviceUnreachable,
            _ => ChannelOpenStatus.ConnectFailed,
        }, $"{operation}: {status}");

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref _stopped) != 0 || args.CharacteristicValue.Length == 0) return;
        _inbound.Writer.TryWrite(args.CharacteristicValue.ToArray());
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Stop();
    }

    private void Stop()
    {
        lock (_shutdown)
        {
            if (_stopped != 0) return;
            Volatile.Write(ref _stopped, 1);
            _shutdown.Cancel();
            _inbound.Writer.TryComplete();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) throw new ArgumentException("A byte-channel read requires a nonempty buffer.", nameof(buffer));
        if (_pendingOffset < _pending.Length)
        {
            int copied = CopyPending(buffer.Span);
            if (copied > 0) return copied;
        }
        while (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_shutdownToken.IsCancellationRequested) return 0;
            if (!_inbound.Reader.TryRead(out var chunk)) continue;
            _pending = chunk;
            _pendingOffset = 0;
            int copied = CopyPending(buffer.Span);
            if (copied > 0) return copied;
        }
        return 0;
    }

    private int CopyPending(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, _pending.Length - _pendingOffset);
        if (count <= 0) return 0;
        _pending.AsSpan(_pendingOffset, count).CopyTo(destination);
        _pendingOffset += count;
        if (_pendingOffset >= _pending.Length)
        {
            _pending = [];
            _pendingOffset = 0;
        }
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_shutdownToken.IsCancellationRequested) throw new IOException("The Sony LE Audio control link is disconnected.");
        cancellationToken.ThrowIfCancellationRequested();
        if (data.IsEmpty) return;
        int chunkSize = Math.Max(20, MaxPayloadSize() - 3);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        var option = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        for (int offset = 0; offset < data.Length; offset += chunkSize)
        {
            var slice = data.Slice(offset, Math.Min(chunkSize, data.Length - offset));
            IBuffer buffer = MemoryMarshal.TryGetArray(slice, out var segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : slice.ToArray().AsBuffer();
            var status = await _write.WriteValueAsync(buffer, option).AsTask(linked.Token).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
            {
                if (status == GattCommunicationStatus.Unreachable) Stop();
                throw new IOException($"Sony LE Audio GATT write failed: {status}");
            }
        }
    }

    private int MaxPayloadSize()
    {
        try
        {
            int pdu = _service.Session.MaxPduSize;
            if (pdu > 3) return pdu;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException) { }
        return 23;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _notify.ValueChanged -= OnValueChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or COMException)
            {
                // The remote CCCD may be unreachable after a disconnect; local handles still must close.
            }
        }
        finally
        {
            try { _service.Dispose(); }
            finally
            {
                try { _device.Dispose(); }
                finally { _shutdown.Dispose(); }
            }
        }
    }
}
