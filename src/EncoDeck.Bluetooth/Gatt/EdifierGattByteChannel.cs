// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Edifier;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>Framed EDIFIER byte stream over the model's official read/write characteristics.</summary>
internal sealed class EdifierGattByteChannel : IDuplexByteChannel
{
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _read;
    private readonly GattCharacteristic _write;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private byte[]? _pending;
    private int _offset;
    private int _disposed;

    private EdifierGattByteChannel(BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic read, GattCharacteristic write)
    {
        _device = device;
        _service = service;
        _read = read;
        _write = write;
        read.ValueChanged += OnValueChanged;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Description => "EDIFIER GATT";

    internal static async Task<ChannelOpenResult> OpenAsync(ulong address, Guid serviceId, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        EdifierGattByteChannel? channel = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Restrict discovery to the selected headset's public address. Never attach to a
            // different nearby pair just because its advertised name or service matches.
            string? endpoint = await DiscoverEndpointAsync(address, timeout.Token).ConfigureAwait(false);
            if (endpoint is null) return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound, "EDIFIER BLE endpoint not found at the selected address.");
            device = await BluetoothLEDevice.FromIdAsync(endpoint).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is null) return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "EDIFIER BLE device not found.");
            if (await device.RequestAccessAsync().AsTask(timeout.Token).ConfigureAwait(false) != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, "EDIFIER BLE access denied.");
            var services = await device.GetGattServicesForUuidAsync(serviceId, BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            foreach (var candidate in services.Services)
            {
                if (service is null) service = candidate;
                else candidate.Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success || service is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, $"EDIFIER GATT service: {services.Status}");
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"EDIFIER GATT characteristics: {characteristics.Status}");
            var read = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == EdifierConstants.GattReadId);
            var write = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == EdifierConstants.GattWriteId);
            if (read is null || write is null ||
                (write.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "EDIFIER read/write characteristic missing.");
            var subscription = read.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : read.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                    : GattClientCharacteristicConfigurationDescriptorValue.None;
            if (subscription == GattClientCharacteristicConfigurationDescriptorValue.None)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "EDIFIER read characteristic cannot notify.");
            channel = new EdifierGattByteChannel(device, service, read, write);
            device = null;
            service = null;
            var status = await read.WriteClientCharacteristicConfigurationDescriptorAsync(subscription).AsTask(timeout.Token).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"EDIFIER GATT subscription: {status}");
            if (channel._device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "EDIFIER disconnected during subscription.");
            var result = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, serviceId, null);
            channel = null;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "EDIFIER GATT connection timed out.");
        }
        catch (UnauthorizedAccessException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, "EDIFIER GATT access denied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"EDIFIER GATT: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            service?.Dispose();
            device?.Dispose();
        }
    }

    private static async Task<string?> DiscoverEndpointAsync(ulong address, CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address, BluetoothAddressType.Public),
            [], DeviceInformationKind.AssociationEndpoint);
        void Added(DeviceWatcher sender, DeviceInformation info) => found.TrySetResult(info.Id);
        void Stopped(DeviceWatcher sender, object args) => found.TrySetResult(null);
        watcher.Added += Added;
        watcher.Stopped += Stopped;
        try
        {
            watcher.Start();
            return await found.Task.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            watcher.Added -= Added;
            watcher.Stopped -= Stopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        _incoming.Writer.TryWrite(args.CharacteristicValue.ToArray());

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _incoming.Writer.TryComplete();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) return 0;
        while (_pending is null)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (!_incoming.Reader.TryRead(out _pending) || _pending.Length == 0) _pending = null;
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _pending.Length) _pending = null;
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var option = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        // The implemented state/noise requests fit in the minimum ATT payload.
        if (data.Length > 20) throw new IOException("EDIFIER command exceeds the supported ATT payload.");
        var result = await _write.WriteValueAsync(data.ToArray().AsBuffer(), option).AsTask(cancellationToken).ConfigureAwait(false);
        if (result != GattCommunicationStatus.Success) throw new IOException($"EDIFIER GATT write: {result}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _incoming.Writer.TryComplete();
        _read.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* A disconnected device may no longer accept CCCD writes. */ }
        finally
        {
            try { _service.Dispose(); }
            finally { _device.Dispose(); }
        }
    }
}
