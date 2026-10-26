// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using EncoDeck.Bluetooth.LeAudio;
using EncoDeck.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EncoDeck.Bluetooth.Gatt;

/// <summary>
/// OPPO control channel over LE Audio: GATT service 079A, write 0100079A, notify 0200079A.
/// Frames are the same as over RFCOMM, so notifications are passed on as one byte stream; each
/// notification is read whole before the next, which keeps frames intact as long as a frame fits
/// one notification (OPPO frames are far below the LE Audio MTU).
/// Both earbuds expose the service but only the current primary answers, and the roles swap
/// (e.g. after one earbud went into the case). Both identities are watched, but only one earbud is
/// subscribed and written at a time: writing to both made the other
/// earbud drop out of the LE Audio stream (mono, then silence). The target is the primary
/// (public address); an earbud that stays silent after a write hands over to the other one.
/// An earbud that connects later joins. Verified on the Enco X4.
/// </summary>
internal sealed class OppoGattByteChannel : IDuplexByteChannel
{
    public static readonly Guid ServiceId = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid WriteId = new("0100079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid NotifyId = new("0200079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(15);

    private sealed class Endpoint(BluetoothLEDevice device, GattSession? session, GattDeviceService service, GattCharacteristic write, GattCharacteristic notify)
    {
        public BluetoothLEDevice Device { get; } = device;
        public GattSession? Session { get; } = session;
        public GattDeviceService Service { get; } = service;
        public GattCharacteristic Write { get; } = write;
        public GattCharacteristic Notify { get; } = notify;
        // Control commands are rare; prefer acknowledged writes so a dropped command surfaces.
        public GattWriteOption WriteOption { get; } = write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        public bool Subscribed { get; set; }
        public OppoGattReplyTracker Replies { get; } = new();
    }

    /// <summary>An earbud that sent nothing this long after a write is taken as not the primary.</summary>
    private static readonly ConcurrentDictionary<ulong, ulong> PreferredTargets = new();

    private readonly Lock _gate = new();
    // Joining, retiring a subscription and writing must not race during an earbud handover.
    private readonly SemaphoreSlim _endpointGate = new(1, 1);
    /// <summary>The bonded earbuds of the pair, connected or not; owned until dispose.</summary>
    private ImmutableArray<BluetoothLEDevice> _candidates = [];
    private ImmutableArray<Endpoint> _endpoints = [];
    private ulong _primaryAddress;
    private Endpoint? _active;
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private byte[]? _pending;
    private int _pendingOffset;
    private int _stopped;
    private int _disposed;

    private OppoGattByteChannel()
    {
        _shutdownToken = _shutdown.Token;
    }

    /// <summary>
    /// The subscribed earbud stops sending events once the other one takes the control role (X4:
    /// taken out of the case); only a write finds that out, so idle state would go stale.
    /// </summary>
    public TimeSpan? KeepAliveInterval => TimeSpan.FromSeconds(30);

    public string Description => $"OPPO GATT 079A ({_endpoints.Length} earbud(s); joined {string.Join(", ", _endpoints.Select(e => e.Device.BluetoothAddress == _primaryAddress ? "primary" : "secondary"))})";

    /// <summary>
    /// Opens the channel on the bonded LE identity at <paramref name="bluetoothAddress"/> and the other
    /// bonded earbud of the pair. Disconnected earbuds join when they connect; this never pairs or pages.
    /// </summary>
    internal static async Task<ChannelOpenResult> OpenAsync(ulong bluetoothAddress, CancellationToken cancellationToken, bool includeSecondary = true)
    {
        var channel = new OppoGattByteChannel { _primaryAddress = bluetoothAddress };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(JoinTimeout);
        try
        {
            var primary = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, BluetoothAddressType.Public).AsTask(timeout.Token).ConfigureAwait(false);
            if (primary is null)
            {
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The paired LE Audio identity is not available yet.");
            }
            // The connector verified the bond via Windows' paired AEPs. A freshly reopened
            // BluetoothLEDevice may still report IsPaired=false during reconnect.
            channel._candidates = [primary];
            List<BluetoothLEDevice> candidates = [primary];
            try
            {
                if (includeSecondary)
                    candidates.AddRange(await LeAudioPairing.FindSecondaryBondsAsync(bluetoothAddress, primary.Name, timeout.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
            {
                // Control through the primary alone still works while it is the primary.
            }
            channel._candidates = [.. candidates];
            foreach (var device in candidates) device.ConnectionStatusChanged += channel.OnConnectionStatusChanged;

            ChannelOpenResult? failure = null;
            ulong preferred = PreferredTargets.GetValueOrDefault(bluetoothAddress, bluetoothAddress);            foreach (var device in candidates.OrderByDescending(d => d.BluetoothAddress == preferred))
            {
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                failure = await channel.TryJoinAsync(device, timeout.Token).ConfigureAwait(false) ?? failure;
                if (!channel._endpoints.IsEmpty) break;
            }
            if (channel._endpoints.IsEmpty)
                return failure ?? ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The earbuds are not connected over LE Audio.");
            var opened = new ChannelOpenResult(ChannelOpenStatus.Opened, channel, ServiceId, null);
            channel = null;
            return opened;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, "OPPO GATT connection timed out or disconnected.");
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or COMException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"OPPO GATT: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Subscribes one connected earbud. Returns a failure, or null once it joined (or it already had).</summary>
    private async Task<ChannelOpenResult?> TryJoinAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        await _endpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await TryJoinCoreAsync(device, cancellationToken).ConfigureAwait(false); }
        finally { _endpointGate.Release(); }
    }

    private async Task<ChannelOpenResult?> TryJoinCoreAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopped != 0 || !_endpoints.IsEmpty) return null;
        }
        GattSession? session = null;
        GattDeviceService? service = null;
        GattCharacteristic? subscribedNotify = null;
        try
        {
            var access = await device.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            // Not "AccessDenied": one earbud refusing must not make the supervisor give up on control.
            if (access != DeviceAccessStatus.Allowed)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"BLE device access: {access}");
            // Keep the LE link while the channel is open, as the RFCOMM socket keeps the ACL.
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(cancellationToken).ConfigureAwait(false);
            if (session is not null) session.MaintainConnection = true;

            var services = await device.GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < services.Services.Count; i++)
            {
                if (i == 0) service = services.Services[i];
                else services.Services[i].Dispose();
            }
            if (services.Status != GattCommunicationStatus.Success) return Failed(services.Status, "OPPO GATT service discovery");
            if (service is null) return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "OPPO GATT service not found.");

            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken).ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success) return Failed(characteristics.Status, "OPPO GATT characteristic discovery");
            var write = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == WriteId);
            var notify = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == NotifyId);
            if (write is null || notify is null || !notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                || (write.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) == 0)
                return ChannelOpenResult.Failed(ChannelOpenStatus.ServiceNotFound, "OPPO GATT write or notify characteristic is missing.");

            notify.ValueChanged += OnValueChanged;
            subscribedNotify = notify;
            var status = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (status != GattCommunicationStatus.Success)
            {
                notify.ValueChanged -= OnValueChanged;
                return Failed(status, "OPPO GATT subscription");
            }
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                notify.ValueChanged -= OnValueChanged;
                return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The earbud disconnected during subscription.");
            }
            var endpoint = new Endpoint(device, session, service, write, notify) { Subscribed = true };
            lock (_gate)
            {
                if (_stopped == 0)
                {
                    _endpoints = _endpoints.Add(endpoint);
                    session = null;
                    service = null;
                    subscribedNotify = null; // The published endpoint now owns this subscription.
                    return null;
                }
            }
            notify.ValueChanged -= OnValueChanged;
            return ChannelOpenResult.Failed(ChannelOpenStatus.DeviceUnreachable, "The channel closed while an earbud joined.");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, $"OPPO GATT join: 0x{ex.HResult:X8}");
        }
        finally
        {
            if (subscribedNotify is not null) subscribedNotify.ValueChanged -= OnValueChanged;
            service?.Dispose();
            session?.Dispose();
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
        // Only noted: the other earbud may push events too, which must not pull commands over to it.
        if (_endpoints.FirstOrDefault(e => ReferenceEquals(e.Notify, sender)) is not { } endpoint) return;
        byte[] data = args.CharacteristicValue.ToArray();
        if (endpoint.Replies.Received(data)) PreferredTargets[_primaryAddress] = endpoint.Device.BluetoothAddress;
        _notifications.Writer.TryWrite(data);
    }

    /// <summary>
    /// The currently subscribed connected earbud; initially prefer the public primary address.
    /// </summary>
    private Endpoint? PickTarget()
    {
        var connected = _endpoints.Where(e => e.Device.ConnectionStatus == BluetoothConnectionStatus.Connected).ToList();
        if (connected.Count == 0) return null;
        var active = Volatile.Read(ref _active);
        if (active is not null && connected.Contains(active))
        {
            return active;
        }
        var target = connected.FirstOrDefault(e => e.Device.BluetoothAddress == _primaryAddress) ?? connected[0];
        Volatile.Write(ref _active, target);
        return target;
    }

    /// <summary>
    /// An earbud that connects joins the channel (it may be the primary now); one going into the
    /// case is fine; the channel ends when no earbud of the pair is connected.
    /// </summary>
    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        try
        {
            var active = _endpoints.FirstOrDefault();
            if (active is not null && active.Device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                PreferOther(active.Device.BluetoothAddress);
                Stop();
            }
            else if (!_candidates.Any(c => c.ConnectionStatus == BluetoothConnectionStatus.Connected)) Stop();
        }
        catch (ObjectDisposedException) { Stop(); }
    }

    private void PreferOther(ulong current)
    {
        var other = _candidates.FirstOrDefault(c => c.BluetoothAddress != current && c.ConnectionStatus == BluetoothConnectionStatus.Connected);
        if (other is not null) PreferredTargets[_primaryAddress] = other.BluetoothAddress;
    }

    private void Stop()
    {
        lock (_gate)
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
        if (buffer.IsEmpty) return 0;
        // A byte stream like RFCOMM: the frame decoder reassembles frames across notifications.
        while (_pending is null)
        {
            if (!await _notifications.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (_shutdownToken.IsCancellationRequested) return 0;
            if (_notifications.Reader.TryRead(out var next)) { _pending = next; _pendingOffset = 0; }
        }
        int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
        _pendingOffset += count;
        if (_pendingOffset == _pending.Length) _pending = null;
        return count;
    }

    /// <summary>Writes each command once, to one subscribed earbud. Unanswered commands are never replayed.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_shutdownToken.IsCancellationRequested) throw new IOException("OPPO GATT earbuds are disconnected.");
        cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        GattCommunicationStatus last = GattCommunicationStatus.Unreachable;
        bool delivered = false;
        bool entered = false;
        try
        {
            await _endpointGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            var target = PickTarget();
            // Keep the first unanswered write's time: frequent requests must not indefinitely
            // postpone detecting a control role change. Switch before the NEXT command; never
            // replay an earlier command whose execution is unknown.
            if (target is not null && target.Replies.IsSilent(Environment.TickCount64)
                && _candidates.Any(c => c.BluetoothAddress != target.Device.BluetoothAddress && c.ConnectionStatus == BluetoothConnectionStatus.Connected))
            {
                PreferOther(target.Device.BluetoothAddress);
                Stop();
                // A different ear must get a new protocol decoder and handshake. Never concatenate
                // two peers' partial frames or carry their subscription state across this boundary.
                throw new IOException("The control ear stopped answering; reconnect through the other earbud.");
            }
            IEnumerable<Endpoint> order = target is null ? [] : _endpoints.Where(e => !ReferenceEquals(e, target)).Prepend(target);
            foreach (var endpoint in order)
            {
                if (endpoint.Device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;
                endpoint.Replies.Sent(data.Span, Environment.TickCount64);
                last = await WriteToAsync(endpoint, data, linked.Token).ConfigureAwait(false);
                if (last != GattCommunicationStatus.Success) { PreferOther(endpoint.Device.BluetoothAddress); Stop(); break; }
                delivered = true;
                Volatile.Write(ref _active, endpoint);
                break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Closed underneath the write: a transport error, as on RFCOMM, not the caller's cancellation.
            throw new IOException("OPPO GATT earbuds disconnected during a write.");
        }
        finally { if (entered) _endpointGate.Release(); }
        if (!delivered) throw new IOException($"OPPO GATT write failed: {last}");
    }

    private static async Task<GattCommunicationStatus> WriteToAsync(Endpoint endpoint, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // ATT carries at most MTU - 3 bytes per write.
        int chunk = Math.Max(20, (endpoint.Session?.MaxPduSize ?? 23) - 3);
        for (int offset = 0; offset < data.Length; offset += chunk)
        {
            var part = data.Slice(offset, Math.Min(chunk, data.Length - offset));
            IBuffer buffer = MemoryMarshal.TryGetArray(part, out var segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : part.ToArray().AsBuffer();
            GattCommunicationStatus status;
            try
            {
                status = await endpoint.Write.WriteValueAsync(buffer, endpoint.WriteOption).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                return GattCommunicationStatus.Unreachable;
            }
            if (status != GattCommunicationStatus.Success) return status;
        }
        return GattCommunicationStatus.Success;
    }

    private async Task ReleaseAsync(Endpoint endpoint)
    {
        endpoint.Notify.ValueChanged -= OnValueChanged;
        try
        {
            if (endpoint.Subscribed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await endpoint.Notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or COMException)
                {
                    // The link may already be gone; local handles still must close.
                }
            }
        }
        finally
        {
            try { endpoint.Service.Dispose(); }
            finally { endpoint.Session?.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        await _endpointGate.WaitAsync().ConfigureAwait(false);
        ImmutableArray<Endpoint> endpoints;
        ImmutableArray<BluetoothLEDevice> candidates;
        lock (_gate)
        {
            endpoints = _endpoints;
            candidates = _candidates;
            _endpoints = [];
            _candidates = [];
        }
        try
        {
            foreach (var endpoint in endpoints) await ReleaseAsync(endpoint).ConfigureAwait(false);
        }
        finally
        {
            foreach (var device in candidates)
            {
                device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                device.Dispose();
            }
            _shutdown.Dispose();
            _endpointGate.Release();
        }
    }
}
