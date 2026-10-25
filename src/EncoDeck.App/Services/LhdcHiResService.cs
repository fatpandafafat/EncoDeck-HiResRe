// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.State;
using EncoDeck.Platform.Lhdc;

namespace EncoDeck.App.Services;

/// <summary>
/// What the earbud reports about its own Hi-Res switch.
/// </summary>
/// <param name="Supported">The earbud has a Hi-Res switch at all.</param>
/// <param name="Enabled">The current state; null when the earbud did not report one.</param>
/// <param name="ViaEncoDeck">True when the value came from EncoDeck's own control channel.</param>
internal sealed record LhdcHiResState(bool Supported, bool? Enabled, bool ViaEncoDeck);

/// <summary>
/// Reads and writes the earbud's Hi-Res switch.
/// <para>
/// The upstream LHDC CLI cannot be used for the earbud EncoDeck drives itself: both want the same
/// RFCOMM control channel, and the second one fails with "address already in use". That earbud is
/// therefore read and written over EncoDeck's own control channel, which speaks the same bytes
/// (feature 0x18). Every other earbud still goes through the CLI, which owns the channel then.
/// </para>
/// </summary>
internal sealed class LhdcHiResService(LhdcBackend backend)
{
    /// <summary>
    /// Reads the switch. Returns null when the earbud cannot be asked at all, and a state with
    /// <c>Supported</c> false when the earbud has no such switch.
    /// </summary>
    /// <param name="waitForDevice">
    /// Waits for EncoDeck's control channel to come back before reading. Switching the earbud's
    /// Hi-Res restarts its Bluetooth link for a moment, so the channel needs a moment to return.
    /// </param>
    public async Task<LhdcHiResState?> ReadAsync(LhdcDevice target, bool waitForDevice = false)
    {
        if (IsControlled(target))
        {
            var features = App.Instance.Features;
            if (features is null) return null;
            if (!features.HasSwitch(DeviceToggle.HiRes)) return new LhdcHiResState(false, null, true);
            if (waitForDevice)
                for (int attempt = 0; attempt < 30 && !features.IsReady; attempt++) await Task.Delay(500);
            if (!features.IsReady) return null;
            // Ask the earbud instead of trusting the value from connect time.
            await App.Instance.Device.RefreshCommand.ExecuteAsync(null);
            bool? enabled = features.Toggle(DeviceToggle.HiRes);
            return new LhdcHiResState(enabled.HasValue, enabled, true);
        }
        var state = await backend.QueryHiResAsync(target.Instance);
        bool supported = state["supported"]?.GetValue<bool>() == true;
        return new LhdcHiResState(supported, supported ? state["enabled"]?.GetValue<bool>() : null, false);
    }

    /// <summary>
    /// Switches Hi-Res on or off and confirms the new state with the earbud. The caller has to
    /// let the LHDC service negotiate the capabilities again afterwards: it caches what the
    /// earbud advertised, so a changed switch only becomes visible after the next negotiation.
    /// </summary>
    public async Task<LhdcHiResState> SetAsync(LhdcDevice target, bool enabled)
    {
        if (IsControlled(target))
        {
            var features = App.Instance.Features;
            if (features is null) throw new InvalidOperationException("EncoDeck has no device control.");
            var result = await features.SetToggleAsync(DeviceToggle.HiRes, enabled);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Detail ?? result.Status.ToString());
            bool? value = features.Toggle(DeviceToggle.HiRes);
            return new LhdcHiResState(value.HasValue, value, true);
        }
        var state = await backend.SetHiResAsync(target.Instance, enabled);
        bool supported = state["supported"]?.GetValue<bool>() == true;
        return new LhdcHiResState(supported, state["enabled"]?.GetValue<bool>(), false);
    }

    /// <summary>True when EncoDeck holds the control channel of this earbud itself.</summary>
    private static bool IsControlled(LhdcDevice target)
    {
        string? current = App.Instance.Device?.Current.Device?.Address.ToFullString();
        return current is not null && Normalize(current) == Normalize(target.Address);
    }

    /// <summary>Bluetooth addresses are compared without separators and case.</summary>
    private static string Normalize(string address) =>
        address.Replace(":", "").Replace("-", "").ToUpperInvariant();
}
