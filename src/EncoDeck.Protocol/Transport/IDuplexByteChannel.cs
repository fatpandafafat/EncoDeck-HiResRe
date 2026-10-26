// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Protocol.Transport;

/// <summary>
/// A connected, ordered byte stream (an RFCOMM socket in production, a fake in tests).
/// Implementations must make <see cref="DisposeAsync"/> abort any pending read.
/// </summary>
public interface IDuplexByteChannel : IAsyncDisposable
{
    /// <summary>Human-readable description for logs (must not contain a full device address).</summary>
    string Description { get; }

    /// <summary>
    /// Set when the peer can go silent without closing the channel (e.g. a GATT earbud that lost
    /// the control role): the controller should query this often so a write notices.
    /// </summary>
    TimeSpan? KeepAliveInterval => null;

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Reads at least one byte, or returns 0 when the remote side closed the channel.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Opens control channels for a specific device.</summary>
public interface IChannelConnector
{
    Task<ChannelOpenResult> OpenAsync(string deviceId, IReadOnlyList<Guid> serviceUuids, CancellationToken cancellationToken);
}

public enum ChannelOpenStatus
{
    Opened,
    /// <summary>None of the requested RFCOMM services exist in the device's SDP record.</summary>
    ServiceNotFound,
    /// <summary>Device is out of range / not connected.</summary>
    DeviceUnreachable,
    /// <summary>Windows denied access (privacy settings, radio off).</summary>
    AccessDenied,
    /// <summary>The service exists but the socket could not be connected.</summary>
    ConnectFailed,
    /// <summary>The selected device's LE control endpoint was not found during bounded discovery.</summary>
    ControlEndpointNotFound,
    /// <summary>
    /// A Classic connection is visible but the bonded LE control link is not ready yet.
    /// Retry with bounded backoff; this does not establish which audio codec Windows is using.
    /// </summary>
    ClassicInsteadOfLeAudio,
}

public sealed record ChannelOpenResult(ChannelOpenStatus Status, IDuplexByteChannel? Channel, Guid? ServiceUuid, string? Detail)
{
    /// <summary>
    /// Resource key of a localized explanation the UI shows instead of the generic failure, when the
    /// connector knows what the user can do about it. <see cref="Detail"/> stays the English log text.
    /// </summary>
    public string? ResourceKey { get; init; }

    public static ChannelOpenResult Failed(ChannelOpenStatus status, string detail) => new(status, null, null, detail);
}
