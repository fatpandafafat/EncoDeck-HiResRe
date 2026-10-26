// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Windows endpoint classes that matter for the LE Audio/Classic split.</summary>
public enum AudioEndpointKind
{
    Other,
    /// <summary>Render endpoint exposed by the LE Audio stack (APXENUM ...&amp;Speaker).</summary>
    LeAudioRender,
    /// <summary>Classic A2DP Audio Sink render endpoint (BTHENUM 0000110B).</summary>
    ClassicA2dpRender,
    /// <summary>Classic Hands-Free render endpoint (BTHENUM 0000111E).</summary>
    ClassicHfpRender,
    /// <summary>Capture endpoint exposed by the LE Audio stack (APXENUM ...&amp;Microphone).</summary>
    LeAudioCapture,
    /// <summary>Classic Hands-Free capture endpoint (BTHENUM 0000111E).</summary>
    ClassicHfpCapture,
}

/// <summary>
/// One render or capture endpoint as seen by PnP. <see cref="ParentInstanceId"/> is the key evidence: APXENUM
/// identifies the LE Audio endpoint, BTHENUM 0000110B the Classic A2DP endpoint. The instance id is
/// the MMDEVAPI node: render endpoints use SWD\MMDEVAPI\{0.0.0...}, capture endpoints {0.0.1...}.
/// </summary>
public sealed record AudioEndpointInfo(
    string InstanceId,
    string FriendlyName,
    string? ParentInstanceId,
    AudioEndpointKind Kind,
    bool IsDisabled)
{
    public string? ContainerId { get; init; }

    /// <summary>Bluetooth identities correlated through the PnP device container.</summary>
    public IReadOnlyList<ulong> AssociatedBluetoothAddresses { get; init; } = [];

    public bool IsActive { get; init; } = true;
    public bool IsStateKnown { get; init; } = true;

    public bool BelongsTo(ulong address) => ParentContainsAddress(address) || AssociatedBluetoothAddresses.Contains(address);

    /// <summary>Same card as <paramref name="address"/> when the parent instance contains the address.</summary>
    public bool ParentContainsAddress(ulong address)
    {
        if (string.IsNullOrEmpty(ParentInstanceId)) return false;
        string text = address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
        return ParentInstanceId.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
