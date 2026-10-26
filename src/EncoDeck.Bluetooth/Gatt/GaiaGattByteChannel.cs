// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands.Moondrop;
using EncoDeck.Protocol.Brands.Sennheiser;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// GAIA GATT carries one unframed message per characteristic notification. The Qualcomm GAIA v1
/// service is shared by several brands (MOONDROP, vivo), so the channel is named after the brand
/// that opened it; Sennheiser uses its own GAIA v3 service.
/// </summary>
internal sealed class GaiaGattByteChannel : IDuplexByteChannel
{
    private readonly BluetoothLEDevice _device;
    private readonly bool _sennheiser;
    private readonly string _label;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _command;
    private readonly GattCharacteristic _response;
    private readonly GattCharacteristic _data;
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly GattWriteOption _writeOption;
    private bool _responseSubscribed;
    private bool _dataSubscribed;
    private int _stopped;
    private int _disposed;

    private GaiaGattByteChannel(BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic command, GattCharacteristic response, GattCharacteristic data, bool sennheiser, string label)
    {
        _device = device;
        _sennheiser = sennheiser;
        _label = label;
        _service = service;
        _command = command;
        _response = response;
        _data = data;
        _shutdownToken = _shutdown.Token;
        _writeOption = command.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        _response.ValueChanged += OnValueChanged;
        _data.ValueChanged += OnValueChanged;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Description => _label;

    /// <summary>Opens the shared GAIA v1 service; <paramref name="brand"/> names the channel in logs (e.g. "MOONDROP", "vivo").</summary>
    internal static Task<ChannelOpenResult> OpenAsync(ulong bluetoothAddress, string brand, CancellationToken cancellationToken) =>
        OpenAsync(bluetoothAddress, false, $"{brand} GAIA GATT", cancellationToken);

    internal static Task<ChannelOpenResult> OpenSennheiserAsync(ulong bluetoothAddress, CancellationToken cancellationToken) =>
        OpenAsync(bluetoothAddress, true, "Sennheiser GAIA v3 GATT", cancellationToken);

    private static async Task<ChannelOpenResult> OpenAsync(ulong bluetoothAddress, bool sennheiser, string label, CancellationToken cancellationToken)
    {
        var serviceId = sennheiser ? SennheiserProfile.GattServiceId : MoondropConstants.GattServiceId;
        var commandId = sennheiser ? new Guid("6333133b-23c1-11e5-b696-feff819cdc9f") : MoondropConstants.GattCommandId;
        var responseId = sennheiser ? new Guid("6333133c-23c1-11e5-b696-feff819cdc9f") : MoondropConstants.GattResponseId;
        var dataId = sennheiser ? new Guid("6333133d-23c1-11e5-b696-feff819cdc9f") : MoondropConstants.GattDataId;
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GaiaGattByteChannel? channel = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Classic pairing does not populate Windows' LE cache. Discover the selected public
            // address first, rather than treating a cache miss as an unreachable audio device.
            string? endpointId = await DiscoverEndpointAsync(bluetoothAddress, timeout.Token).ConfigureAwait(false);
            if (endpointId is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound,
                    "The selected headset's Bluetooth LE control endpoint was not found. Audio can remain connected; make the headset discoverable and retry controls.");
            device = await BluetoothLEDevice.FromIdAsync(endpointId).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound, "Windows could not open the discovered Bluetooth LE control endpoint.");
            var access = await device.RequestAccessAsync().AsTask(timeout.Token).ConfigureAwait(false);
            if (access != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"BLE device access: {access}");

            var services = await device.GetGattServicesForUuidAsync(serviceId, BluetoothCacheMode.Uncached)
                .AsTask(timeout.Token).ConfigureAwait(false);
            // Own every returned service even when discovery reports an error.
            for (int i = 0; i < services.Services.Count; i++)
            {
                if (i == 0) service = services.Services[i];
                else services.Services[i].Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success) return Failed(services.Status, "GAIA service discovery");
            if (service is null) return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "GAIA GATT service not found.");

            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success) return Failed(characteristics.Status, "GAIA characteristic discovery");
            var command = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == commandId);
            var response = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == responseId);
            var data = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == dataId);
            if (command is null || response is null || data is null)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "GAIA command, response or data characteristic is missing.");
            if ((command.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "GAIA command characteristic is not writable.");

            channel = new GaiaGattByteChannel(device, service, command, response, data, sennheiser, label);
            device = null;
            service = null;
            using var connected = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, channel._shutdownToken);
            channel._responseSubscribed = true;
            var status = await SubscribeAsync(response, connected.Token).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success) return Failed(status, "GAIA response subscription");
            if (!sennheiser || (data.CharacteristicProperties & (GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)) != 0)
            {
                channel._dataSubscribed = true;
                status = await SubscribeAsync(data, connected.Token).ConfigureAwait(false);
                if (status != GattCommunicationStatus.Success) return Failed(status, "GAIA data subscription");
            }
            connected.Token.ThrowIfCancellationRequested();
            if (channel._device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "GAIA device disconnected during setup.");
            var result = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, serviceId, null);
            channel = null;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "GAIA GATT connection timed out or disconnected.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"GAIA GATT: 0x{ex.HResult:X8}");
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
        // Public address equality binds LE discovery to the selected classic device. A name,
        // nearby GAIA advertisement, or random address is not proof of the same headset.
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

    private static async Task<GattCommunicationStatus> SubscribeAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        var properties = characteristic.CharacteristicProperties;
        var configuration = properties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : properties.HasFlag(GattCharacteristicProperties.Indicate)
                ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : throw new IOException("GAIA characteristic does not support notifications or indications.");
        return await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(configuration)
            .AsTask(cancellationToken).ConfigureAwait(false);
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (Volatile.Read(ref _stopped) == 0 && args.CharacteristicValue.Length > 0)
            {
                byte[] bytes = args.CharacteristicValue.ToArray();
                if (_sennheiser)
                {
                    try { bytes = SennheiserGattFraming.WrapNotification(bytes); }
                    catch (InvalidDataException) { return; }
                }
                _notifications.Writer.TryWrite(bytes);
            }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Stop();
    }

    private void Stop()
    {
        // Finish concurrent Stop callbacks before Dispose releases the cancellation source.
        lock (_shutdown)
        {
            if (_stopped != 0) return;
            Volatile.Write(ref _stopped, 1);
            _shutdown.Cancel();
            _notifications.Writer.TryComplete();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty) throw new ArgumentException("A GAIA message requires a nonempty read buffer.", nameof(buffer));
        while (await _notifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_shutdownToken.IsCancellationRequested) return 0;
            if (!_notifications.Reader.TryPeek(out var message)) continue;
            // Splitting or combining notifications would destroy GAIA's only message boundary.
            if (message.Length > buffer.Length) throw new IOException($"GAIA read buffer is too small ({buffer.Length}/{message.Length}).");
            if (!_notifications.Reader.TryRead(out message)) continue;
            message.AsSpan().CopyTo(buffer.Span);
            return message.Length;
        }
        return 0;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_shutdownToken.IsCancellationRequested) throw new IOException("GAIA GATT device is disconnected.");
        cancellationToken.ThrowIfCancellationRequested();
        if (data.IsEmpty) return;
        if (_sennheiser) data = SennheiserGattFraming.UnwrapCommand(data.Span);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        IBuffer buffer = MemoryMarshal.TryGetArray(data, out var segment)
            ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
            : data.ToArray().AsBuffer();
        var status = await _command.WriteValueAsync(buffer, _writeOption).AsTask(linked.Token).ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success)
        {
            if (status == GattCommunicationStatus.Unreachable) Stop();
            throw new IOException($"GAIA GATT write failed: {status}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _response.ValueChanged -= OnValueChanged;
        _data.ValueChanged -= OnValueChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (_responseSubscribed) await UnsubscribeAsync(_response, timeout.Token).ConfigureAwait(false);
            if (_dataSubscribed) await UnsubscribeAsync(_data, timeout.Token).ConfigureAwait(false);
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

    private static async Task UnsubscribeAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        try
        {
            await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            // Disconnect/cancel may make the remote CCCD unreachable; local handles still must close.
        }
    }
}
