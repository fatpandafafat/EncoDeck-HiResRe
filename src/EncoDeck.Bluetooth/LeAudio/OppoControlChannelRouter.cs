// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Protocol.Transport;

namespace EncoDeck.Bluetooth.LeAudio;

/// <summary>Chooses the transport before any Classic access, SDP lookup or socket operation.</summary>
internal static class OppoControlChannelRouter
{
    internal static async Task<ChannelOpenResult> OpenAsync(
        Func<Task<bool>> isLeBonded,
        Func<Task<ChannelOpenResult>> openGatt,
        Func<Task<ChannelOpenResult>> openClassic)
    {
        bool leBonded;
        try
        {
            leBonded = await isLeBonded().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed,
                $"Could not determine the LE Audio pairing state (0x{ex.HResult:X8}): {ex.Message}; Classic control was not opened.");
        }

        if (!leBonded) return await openClassic().ConfigureAwait(false);

        // A missing/busy LE service is retryable. It never licenses an RFCOMM fallback, which can
        // move the earbuds off LC3 even while Windows still reports a Classic ACL as connected.
        var result = await openGatt().ConfigureAwait(false);
        return result.Status is ChannelOpenStatus.AccessDenied or ChannelOpenStatus.ServiceNotFound
            ? ChannelOpenResult.Failed(ChannelOpenStatus.ConnectFailed, result.Detail ?? "OPPO GATT is not ready.")
            : result;
    }
}
