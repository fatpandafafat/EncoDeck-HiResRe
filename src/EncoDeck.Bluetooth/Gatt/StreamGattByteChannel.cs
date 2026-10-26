// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Protocol.Brands;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// Ordinary byte stream over a write characteristic and a notifying characteristic (Sony MDR over
/// GATT, AKG BES, Airoha RACE). The brand codecs re-frame the stream, so notifications may split or
/// join frames. The LE endpoint is bound to the selected headset's address; only endpoints marked
/// <see cref="GattStreamEndpoint.NameMatchedLeFallback"/> may fall back to one name-matched LE advertiser.
/// </summary>
internal sealed class StreamGattByteChannel : IDuplexByteChannel
{
    /// <summary>Minimum ATT payload; unacknowledged writes are split to fit it.</summary>
    private const int MinimumAttPayload = 20;

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _notify;
    private readonly GattCharacteristic _write;
    private readonly GattWriteOption _writeOption;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private byte[]? _pending;
    private int _offset;
    private int _disposed;

    private StreamGattByteChannel(BluetoothLEDevice device, GattDeviceService service, GattCharacteristic notify, GattCharacteristic write, string name)
    {
        _device = device;
        _service = service;
        _notify = notify;
        _write = write;
        Description = name;
        _writeOption = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        notify.ValueChanged += OnValueChanged;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string Description { get; }

    /// <summary>
    /// Opens the endpoint at the selected headset's address; endpoints that allow it then fall back to
    /// one name-matched LE advertiser (see <see cref="LeCompanionResolver"/>).
    /// </summary>
    internal static Task<ChannelOpenResult> OpenAsync(ulong address, string? name, GattStreamEndpoint endpoint, ILogger logger, CancellationToken cancellationToken) =>
        LeCompanionResolver.OpenAsync(address, name, endpoint, LeCompanionCache.Shared,
            (target, expectedName, ct) => OpenAtAsync(address, target, expectedName, endpoint, ct), ScanAsync, logger, cancellationToken);

    /// <summary>Opens <paramref name="endpoint"/> at the classic address (<paramref name="target"/> null) or at an LE companion.</summary>
    private static async Task<ChannelOpenResult> OpenAtAsync(ulong address, LeTarget? target, string? expectedName, GattStreamEndpoint endpoint, CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        StreamGattByteChannel? channel = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (target is not null)
            {
                // A companion chosen by LeCompanionResolver: exactly one advertiser matched the paired name.
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(target.Address,
                    target.IsRandom ? BluetoothAddressType.Random : BluetoothAddressType.Public).AsTask(timeout.Token).ConfigureAwait(false);
                if (device is null)
                    return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound, $"{endpoint.Name}: LE companion not found.");
                if (expectedName is not null && !string.IsNullOrEmpty(device.Name) && !LeCompanionResolver.NameMatches(expectedName, device.Name))
                    return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, $"{endpoint.Name}: LE companion reports a different name.");
            }
            else
            {
                // Never attach to a different nearby headset because its name or service matches.
                string? id = await DiscoverEndpointAsync(address, timeout.Token).ConfigureAwait(false);
                device = id is not null
                    ? await BluetoothLEDevice.FromIdAsync(id).AsTask(timeout.Token).ConfigureAwait(false)
                    : await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token).ConfigureAwait(false);
                if (device is null)
                    return ChannelOpenResult.Failed(ChannelOpenStatus.ControlEndpointNotFound, $"{endpoint.Name}: LE endpoint not found at the selected address.");
            }
            if (await device.RequestAccessAsync().AsTask(timeout.Token).ConfigureAwait(false) != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"{endpoint.Name}: access denied.");
            var services = await device.GetGattServicesForUuidAsync(endpoint.Service, BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            foreach (var candidate in services.Services)
            {
                if (service is null) service = candidate;
                else candidate.Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success || service is null)
                return ChannelOpenResult.Failed(services.Status == GattCommunicationStatus.Success ? ChannelOpenStatus.ServiceNotFound : ChannelOpenStatus.ConnectFailed,
                    $"{endpoint.Name} service: {services.Status}");
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name} characteristics: {characteristics.Status}");
            const GattCharacteristicProperties writable = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse;
            const GattCharacteristicProperties notifying = GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate;
            // The documented characteristic first; otherwise the first one with the right property.
            var write = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == endpoint.Write && (c.CharacteristicProperties & writable) != 0)
                ?? characteristics.Characteristics.FirstOrDefault(c => (c.CharacteristicProperties & writable) != 0);
            var notify = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == endpoint.Notify && (c.CharacteristicProperties & notifying) != 0)
                ?? characteristics.Characteristics.FirstOrDefault(c => (c.CharacteristicProperties & notifying) != 0);
            if (write is null || notify is null || (write.CharacteristicProperties & writable) == 0 || (notify.CharacteristicProperties & notifying) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, $"{endpoint.Name}: write or notify characteristic missing.");
            var subscription = notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            channel = new StreamGattByteChannel(device, service, notify, write, endpoint.Name);
            device = null;
            service = null;
            var status = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(subscription).AsTask(timeout.Token).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name} subscription: {status}");
            if (channel._device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, $"{endpoint.Name}: disconnected during subscription.");
            var result = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, endpoint.Service, null);
            channel = null;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name}: connection timed out.");
        }
        catch (UnauthorizedAccessException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.AccessDenied, $"{endpoint.Name}: access denied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"{endpoint.Name}: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            service?.Dispose();
            device?.Dispose();
        }
    }

    /// <summary>Active LE scan for <see cref="LeCompanionResolver.ScanWindow"/>; name and service UUIDs are merged per advertiser.</summary>
    private static async Task<IReadOnlyList<LeAdvertisement>> ScanAsync(CancellationToken cancellationToken)
    {
        var seen = new Dictionary<(ulong, bool), (string? Name, HashSet<Guid> Services)>();
        var gate = new object();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var key = (args.BluetoothAddress, args.BluetoothAddressType == BluetoothAddressType.Random);
            lock (gate)
            {
                var entry = seen.TryGetValue(key, out var known) ? known : (null, []);
                if (!string.IsNullOrEmpty(args.Advertisement.LocalName)) entry.Name = args.Advertisement.LocalName;
                foreach (var uuid in args.Advertisement.ServiceUuids) entry.Services.Add(uuid);
                seen[key] = entry;
            }
        }
        watcher.Received += Received;
        try
        {
            watcher.Start();
            await Task.Delay(LeCompanionResolver.ScanWindow, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            watcher.Received -= Received;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
        lock (gate)
            return seen.Select(e => new LeAdvertisement(new(e.Key.Item1, e.Key.Item2), e.Value.Name, e.Value.Services.ToArray())).ToArray();
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

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (args.CharacteristicValue.Length > 0) _incoming.Writer.TryWrite(args.CharacteristicValue.ToArray());
    }

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
        // Acknowledged writes may use long writes; unacknowledged ones must fit the minimum ATT payload.
        int chunk = _writeOption == GattWriteOption.WriteWithResponse ? Math.Max(data.Length, 1) : MinimumAttPayload;
        for (int offset = 0; offset < data.Length; offset += chunk)
        {
            var part = data.Slice(offset, Math.Min(chunk, data.Length - offset));
            var buffer = MemoryMarshal.TryGetArray(part, out var segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : part.ToArray().AsBuffer();
            var result = await _write.WriteValueAsync(buffer, _writeOption).AsTask(cancellationToken).ConfigureAwait(false);
            if (result != GattCommunicationStatus.Success) throw new IOException($"{Description} write: {result}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _incoming.Writer.TryComplete();
        _notify.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
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
