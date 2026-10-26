// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Brands.Usb;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Bluetooth.Usb;

/// <summary>
/// Barracuda X (1532:0550) headset settings in the device state. A setting is offered only after a valid
/// read-back while the headset is linked, and is withdrawn as soon as the link is not confirmed. A write
/// shows the new value only once the headset reports it back; otherwise the state keeps (or is corrected to)
/// what the headset reports afterwards. Every HID session runs under <c>gate</c>, the same lock the battery
/// polling holds, so settings and battery traffic never interleave on the receiver.
/// </summary>
internal sealed class UsbHeadsetSettingsController(
    string deviceId,
    IDeviceStateStore store,
    SemaphoreSlim gate,
    Func<IReadOnlyList<RazerPaSetting>, CancellationToken, Task<IReadOnlyDictionary<RazerPaSetting, byte[]>>> read,
    Func<RazerPaSetting, byte[], CancellationToken, Task<bool>> write,
    ILogger? logger = null,
    TimeProvider? time = null)
{
    /// <summary>A linked headset's settings are read again this often, to follow changes made elsewhere.</summary>
    public static readonly TimeSpan ResyncInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<RazerPaSetting, int> _latest = [];
    private DateTimeOffset? _completeAt;
    private volatile bool _stale;
    private volatile bool _linked;

    /// <summary>
    /// Called by the battery poll while it holds the gate, with whether the headset just answered over the
    /// radio link. Reads the settings on (re)connection, after an unconfirmed write, when some are still
    /// missing, after <see cref="ResyncInterval"/>, or when <paramref name="force"/> is set.
    /// </summary>
    public async Task SyncAsync(bool linked, bool force, CancellationToken ct)
    {
        if (!linked)
        {
            Clear();
            return;
        }
        bool complete = Offered().Count == BarracudaXSettingValues.Offered.Count;
        if (_linked && complete && !force && !_stale && _completeAt is { } at && _time.GetUtcNow() - at < ResyncInterval) return;
        _stale = false;
        try
        {
            var values = await read(BarracudaXSettingValues.Offered, ct).ConfigureAwait(false);
            _linked = true;
            int applied = Apply(values);
            _completeAt = applied == BarracudaXSettingValues.Offered.Count ? _time.GetUtcNow() : null;
            logger?.LogInformation("Barracuda X settings read back: {Count} of {Total}", applied, BarracudaXSettingValues.Offered.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The battery answered, so the link is up: keep what was confirmed earlier and retry next poll.
            _stale = true;
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            logger?.LogWarning("Barracuda X settings read failed: 0x{HResult:X8}", ex.HResult);
        }
    }

    /// <summary>Withdraws every setting (headset off, out of range, receiver gone or unreadable).</summary>
    public void Clear()
    {
        _linked = false;
        _completeAt = null;
        store.TryUpdate(deviceId, s => s.Profile is UsbHeadsetProfile { Settings.Count: > 0 } profile || s.Equalizer is not null || s.CustomEqualizers is not null
            || s.Options.ContainsKey(AutoOffOption) || s.Toggles.ContainsKey(DeviceToggle.DoNotDisturb)
            ? s with
            {
                Profile = s.Profile is UsbHeadsetProfile usb ? usb.WithSettings([]) : s.Profile,
                Equalizer = null,
                CustomEqualizers = null,
                Options = s.Options.Remove(AutoOffOption),
                Toggles = s.Toggles.Remove(DeviceToggle.DoNotDisturb),
            }
            : s);
    }

    public Task<CommandResult> SetEqualizerPresetAsync(string presetId, CancellationToken ct) =>
        BarracudaXSettingValues.PresetValue(presetId) is { } raw
            ? WriteAsync(RazerPaSetting.EqPreset, [raw], "equalizer", ct)
            : Task.FromResult(CommandResult.Unsupported($"Unknown Barracuda X preset {presetId}."));

    public Task<CommandResult> SelectCustomEqualizerAsync(int id, CancellationToken ct) =>
        id == BarracudaXSettingValues.CustomPresetId
            ? WriteAsync(RazerPaSetting.EqPreset, [BarracudaXSettingValues.CustomPresetId], "equalizer", ct)
            : Task.FromResult(CommandResult.Unsupported("The Barracuda X has a single custom EQ."));

    public Task<CommandResult> SaveCustomEqualizerAsync(CustomEqualizer preset, CancellationToken ct) =>
        preset.Id == BarracudaXSettingValues.CustomPresetId && BarracudaXSettingValues.Bands(preset.Bands.Select(b => b.Gain).ToArray()) is { } bands
            ? WriteAsync(RazerPaSetting.CustomEq, bands, "custom-eq", ct)
            : Task.FromResult(new CommandResult(CommandStatus.Rejected, "Band values outside the Barracuda X range."));

    public Task<CommandResult> SetToggleAsync(DeviceToggle toggle, bool enabled, CancellationToken ct) =>
        toggle == DeviceToggle.DoNotDisturb
            ? WriteAsync(RazerPaSetting.DoNotDisturb, [enabled ? (byte)1 : (byte)0], $"toggle:{toggle}", ct)
            : Task.FromResult(CommandResult.Unsupported($"{toggle} is not a Barracuda X setting."));

    /// <summary>Id of the idle power-off setting in <see cref="DeviceState.Options"/> (minutes, 0 = never).</summary>
    public const string AutoOffOption = "usb-auto-power-off";

    /// <summary>The idle power-off as a shared device option; a reported value outside the list is shown as reported.</summary>
    public static DeviceOption AutoOffOptionFor(int minutes)
    {
        var values = BarracudaXSettingValues.AutoOffMinutes.Contains(minutes) ? BarracudaXSettingValues.AutoOffMinutes : [.. BarracudaXSettingValues.AutoOffMinutes, minutes];
        var choices = values.Select(m => m == 0 ? new DeviceOptionChoice(0, "AutoOff_Off")
            : new DeviceOptionChoice(m, "AutoOff_Minutes", m.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return new DeviceOption(AutoOffOption, "AutoOff_Title", minutes, choices, 10) { DescriptionKey = "AutoOff_IdleDescription" };
    }

    public Task<CommandResult> SetOptionAsync(string id, int value, CancellationToken ct) =>
        id != AutoOffOption ? Task.FromResult(CommandResult.Unsupported($"Option {id} is not a Barracuda X setting."))
        : BarracudaXSettingValues.AutoOffMinutes.Contains(value)
            ? WriteAsync(RazerPaSetting.AutoOff, [(byte)value], "auto-power-off", ct)
            : Task.FromResult(new CommandResult(CommandStatus.Rejected, "Unsupported idle time."));

    private IReadOnlyCollection<RazerPaSetting> Offered() =>
        store.Current.Device?.DeviceId == deviceId && store.Current.Profile is UsbHeadsetProfile profile ? profile.Settings : [];

    private async Task<CommandResult> WriteAsync(RazerPaSetting setting, byte[] values, string pending, CancellationToken ct)
    {
        if (!_linked || !Offered().Contains(setting)) return CommandResult.NotConnected;
        int ticket;
        lock (_latest) _latest[setting] = ticket = _latest.GetValueOrDefault(setting) + 1;
        var command = new PendingCommand(Guid.NewGuid(), pending, _time.GetUtcNow());
        store.TryUpdate(deviceId, s => s with { PendingCommands = s.PendingCommands.Add(command) });
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Writes queued while the receiver was busy collapse to the newest value (EQ slider drags).
                lock (_latest)
                    if (_latest[setting] != ticket) return new(CommandStatus.Cancelled, "Superseded by a newer value.");
                if (!_linked || !Offered().Contains(setting)) return CommandResult.NotConnected;
                if (await write(setting, values, ct).ConfigureAwait(false))
                {
                    Apply(new Dictionary<RazerPaSetting, byte[]> { [setting] = values });
                    return CommandResult.Ok;
                }
                logger?.LogWarning("Barracuda X setting {Setting:X2} was not confirmed by read-back", (byte)setting);
                // Show what the headset really has now; if even that is unreadable, the next poll re-reads.
                var actual = await read([setting], ct).ConfigureAwait(false);
                if (actual.ContainsKey(setting)) Apply(actual);
                else _stale = true;
                return new(CommandStatus.Rejected, "The headset did not report the written value back.");
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _stale = true;
            return new(CommandStatus.Cancelled);
        }
        catch (OperationCanceledException)
        {
            _stale = true;
            return new(CommandStatus.Timeout, "The headset did not answer in time.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _stale = true;
            logger?.LogWarning("Barracuda X setting write failed: 0x{HResult:X8}", ex.HResult);
            return new(CommandStatus.TransportError, "USB headset access is unavailable.");
        }
        finally
        {
            store.TryUpdate(deviceId, s => s with { PendingCommands = s.PendingCommands.Remove(command) });
        }
    }

    /// <summary>Applies read-back values that are well-formed; returns how many settings are now offered.</summary>
    private int Apply(IReadOnlyDictionary<RazerPaSetting, byte[]> values)
    {
        int offered = 0;
        var now = _time.GetUtcNow();
        store.TryUpdate(deviceId, s =>
        {
            if (s.Profile is not UsbHeadsetProfile profile) return s;
            var confirmed = new HashSet<RazerPaSetting>(profile.Settings);
            var next = s;
            foreach (var setting in BarracudaXSettingValues.Offered)
            {
                if (!values.TryGetValue(setting, out var raw) || !IsWellFormed(setting, raw)) continue;
                confirmed.Add(setting);
                next = setting switch
                {
                    // An unknown preset id is kept as its raw value and shown as such, never mapped to a guess.
                    RazerPaSetting.EqPreset => next with { Equalizer = Observed.From(new EqualizerState(BarracudaXSettingValues.PresetId(raw[0]), raw[0]), now, StateSource.QueryResponse) },
                    RazerPaSetting.CustomEq => next with
                    {
                        CustomEqualizers = Observed.From<IReadOnlyList<CustomEqualizer>>(
                            [new CustomEqualizer(BarracudaXSettingValues.CustomPresetId, "", BarracudaXSettingValues.BandMin, BarracudaXSettingValues.BandMax, false,
                                raw.Select((b, i) => new EqualizerBand(BarracudaXSettingValues.BandFrequencies[i], BarracudaXSettingValues.Gain(b))).ToArray())],
                            now, StateSource.QueryResponse),
                    },
                    RazerPaSetting.DoNotDisturb => next with { Toggles = next.Toggles.SetItem(DeviceToggle.DoNotDisturb, Observed.From(raw[0] == 1, now, StateSource.QueryResponse)) },
                    RazerPaSetting.AutoOff => next with { Options = next.Options.SetItem(AutoOffOption, Observed.From(AutoOffOptionFor(raw[0]), now, StateSource.QueryResponse)) },
                    _ => next,
                };
            }
            // The custom EQ counts as selected exactly while the preset register says custom (FF).
            if (next.CustomEqualizers is { Value: [var custom] } list)
            {
                bool selected = next.Equalizer?.Value.RawPresetValue == BarracudaXSettingValues.CustomPresetId;
                if (custom.IsSelected != selected) next = next with { CustomEqualizers = list with { Value = [custom with { IsSelected = selected }] } };
            }
            offered = confirmed.Count;
            return next with { Profile = profile.WithSettings(confirmed) };
        });
        return offered;
    }

    private static bool IsWellFormed(RazerPaSetting setting, byte[] raw) => setting switch
    {
        RazerPaSetting.EqPreset or RazerPaSetting.AutoOff => raw.Length == 1,
        _ => RazerPaSettings.IsValid(RazerPaSettings.BarracudaX, setting, raw),
    };
}
