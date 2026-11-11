// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Nodes;
using EncoDeck.App.Services;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Lhdc;
using EncoDeck.Platform.Updates;
using EncoDeck.Platform.Packaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

public sealed partial class LhdcSettings : UserControl
{
    private sealed record Choice(int Value, string Label);
    private CancellationTokenSource? _download;
    private bool _busy, _syncing, _updating;

    // What the user asked for, kept across the capability refreshes that rebuild the lists.
    private int _rate = 48000, _bits = 24, _preferredCanonical = 400;

    // The capabilities the LHDC service negotiated with the earbud, as it published them.
    private string _capabilities = "";
    private int _mediaMtu, _maxKbps;
    private int[] _allowedRates = [];
    private int[] _allowedDepths = [];
    private Dictionary<int, int[]> _bitratesByRate = [];

    // The earbud's own Hi-Res switch, read over the control channel.
    private bool _hiResUnsupported;
    private bool? _hiResEnabled;

    // True while the earbud advertises more than the negotiated capabilities describe.
    private bool _capabilitiesStale;
    // True while the driver has not published any capability set for the earbud at all: the
    // bitrate list then shows the fallback table, not what the earbud can really do.
    private bool _capabilitiesMissing;
    // The driver is only asked to renegotiate once per device, so a driver that cannot
    // renegotiate does not restart its session over and over.
    private bool _negotiated;
    private LhdcHiResService? _hiRes;
    // True while a deployment is running, so a late progress line cannot bring the progress
    // area back after the deployment is over.
    private bool _deploying;

    // The observed line is rebuilt whenever the live bitrate changes, so the parts that stay the
    // same are kept apart from the bitrate itself.
    private string _observedHead = "", _observedTail = "";
    // The bitrate the LHDC service is really playing, as it publishes it.
    private int _activeKbps;
    private DispatcherQueueTimer? _liveTimer;
    private bool _liveQuery;

    public LhdcSettings()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // The Hi-Res switch of the earbud EncoDeck drives itself is read over EncoDeck's own
            // control channel, so the service has to exist even when the LHDC component is not
            // installed. Only the CLI branch needs the component's backend, and that branch needs
            // the component anyway.
            _hiRes = new LhdcHiResService(LhdcCompanion.IsReady ? LhdcCompanion.Backend : new LhdcBackend("", ""));
            Cancel.Content = Loc.Get("Lhdc_Cancel");
            CheckUpdates.Content = Loc.Get("Component_CheckUpdates");
            Uninstall.Content = Loc.Get("Component_Uninstall");
            Devices.Header = Loc.Get("Lhdc_Device");
            Rate.Header = Loc.Get("Lhdc_Rate");
            Depth.Header = Loc.Get("Lhdc_Depth");
            Bitrate.Header = Loc.Get("Lhdc_Bitrate");
            Adaptive.Content = Loc.Get("Lhdc_Adaptive");
            Apply.Content = Loc.Get("Lhdc_Apply");
            Restore.Content = Loc.Get("Lhdc_Restore");
            Refresh.Content = Loc.Get("Lhdc_Refresh");
            RebuildChoices();
            Status.Text = StatusText();
            Sync();
            if (LhdcCompanion.IsReady)
            {
                await Operate(RefreshDevices);
            }
            // At most once a day, quietly: a later component release can arrive without an app update.
            try
            {
                if (await LhdcCompanion.CheckForUpdateAsync(App.Instance.Host.Settings.Current.IncludePrereleaseUpdates, force: false) is not null && !_busy)
                {
                    Status.Text = StatusText();
                    Sync();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            // The service publishes the bitrate it is really playing, so the observed line can
            // follow it while the earbuds keep playing.
            _liveTimer = DispatcherQueue.CreateTimer();
            _liveTimer.Interval = TimeSpan.FromSeconds(3);
            _liveTimer.Tick += OnLiveTick;
            _liveTimer.Start();
        };
        Unloaded += (_, _) => { _download?.Cancel(); _liveTimer?.Stop(); };
    }

    /// <summary>The state, and which component build is installed (an app update keeps using it until a newer pin is downloaded).</summary>
    private static string StatusText()
    {
        string text = Loc.Get(LhdcCompanion.StatusKey);
        if (LhdcCompanion.Component.Installed is { } installed)
            text += Environment.NewLine + Loc.Format("Lhdc_Version", installed.Version);
        if (LhdcCompanion.UpdateTarget is { } update) text += Environment.NewLine + Loc.Format("Lhdc_UpdateAvailable", update.Version);
        return text;
    }

    private void Sync()
    {
        Download.Content = LhdcCompanion.UpdateAvailable ? Loc.Format("Lhdc_Update", LhdcCompanion.PackageMegabytes)
            : Loc.Format("Lhdc_DownloadSize", LhdcCompanion.PackageMegabytes);
        Editor.Visibility = LhdcCompanion.IsReady ? Visibility.Visible : Visibility.Collapsed;
        Download.Visibility = (LhdcCompanion.CanDownload || PackageIdentity.IsPackaged) && _download is null ? Visibility.Visible : Visibility.Collapsed;
        Download.IsEnabled = !_busy && LhdcCompanion.CanDownload;
        if (PackageIdentity.IsPackaged) Download.Content = Loc.Get("Lhdc_StoreDisabled");
        StandaloneLink.Visibility = PackageIdentity.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
        bool manage = !PackageIdentity.IsPackaged && LhdcCompanion.StatusKey is "Lhdc_Ready" or "Lhdc_Missing" or "Lhdc_Unpublished";
        CheckUpdates.Visibility = manage && _download is null ? Visibility.Visible : Visibility.Collapsed;
        Uninstall.Visibility = manage && LhdcCompanion.IsDownloaded && _download is null ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdates.IsEnabled = Uninstall.IsEnabled = !_busy;
        Devices.IsEnabled = Rate.IsEnabled = Depth.IsEnabled = Bitrate.IsEnabled = Adaptive.IsEnabled = Refresh.IsEnabled = !_busy;
        // The switch only reflects a state the earbud confirmed; an unknown state stays disabled.
        HiRes.IsEnabled = !_busy && _hiResEnabled.HasValue;
        Apply.IsEnabled = Restore.IsEnabled = !_busy && Devices.SelectedItem is LhdcDevice;
    }

    /// <summary>
    /// Runs one operation at a time. Its progress shows in the status line, its result or failure in the message
    /// bar; the status line returns to the component's state afterwards whatever happened.
    /// </summary>
    private async Task Operate(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        CardNotice.Hide(Notice);
        Sync();
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CardNotice.ShowError(Notice, ex); }
        finally
        {
            _busy = false;
            Status.Text = StatusText();
            Sync();
        }
    }

    private async Task RefreshDevices()
    {
        var backend = LhdcCompanion.Backend;
        var targets = await backend.QueryAsync(null, "targets");
        string? selected = (Devices.SelectedItem as LhdcDevice)?.Instance ?? Text(targets, "selected_instance");
        var devices = (targets["devices"] as JsonArray ?? [])
            .Select(d => new LhdcDevice(Text(d, "instance") ?? "", Text(d, "name") ?? "", Text(d, "address") ?? ""))
            .Where(d => d.Instance.Length > 0).ToArray();
        _syncing = true;
        try
        {
            Devices.ItemsSource = devices;
            Devices.SelectedItem = devices.FirstOrDefault(d => d.Instance.Equals(selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncing = false; }
        await ReadStatus();
    }

    private async Task ReadStatus(bool waitForDevice = false)
    {
        Observed.Text = "";
        _observedHead = "";
        _observedTail = "";
        _activeKbps = 0;
        HiResRow.Visibility = Visibility.Collapsed;
        _capabilities = "";
        _mediaMtu = 0;
        _maxKbps = 0;
        _allowedRates = [];
        _allowedDepths = [];
        _bitratesByRate = [];
        _hiResEnabled = null;
        _hiResUnsupported = false;
        _capabilitiesMissing = false;
        if (Devices.SelectedItem is not LhdcDevice device)
        {
            Observed.Text = Loc.Get("Lhdc_SelectDevice");
            RebuildChoices();
            UpdateHiRes();
            return;
        }
        var backend = LhdcCompanion.Backend;
        var target = await backend.QueryAsync(device.Instance, "target");
        bool found = target["found"]?.GetValue<bool>() == true;
        bool connected = target["connected"]?.GetValue<bool>() == true;
        bool heyMelody = target["hey_melody"]?.GetValue<bool>() == true;
        var inventory = await backend.QueryAsync(device.Instance, "inspect");
        var audio = await backend.QueryAsync(device.Instance, "audio-status");
        var entry = (inventory["devices"] as JsonArray ?? []).FirstOrDefault(d => string.Equals(Text(d, "instance_id"), device.Instance, StringComparison.OrdinalIgnoreCase));
        string service = Text(entry, "service") ?? "";
        string mode = service == "BthA2dp" ? Loc.Get("Lhdc_WindowsMode")
            : service == "lhdc-transport" && audio["running"]?.GetValue<bool>() == true ? "LHDC V5" : Loc.Get("Lhdc_UnknownMode");
        int active = audio["active_kbps"]?.GetValue<int>() ?? 0;
        await ReadCapabilitiesAsync(device, audio);

        if (heyMelody)
        {
            HiResRow.Visibility = Visibility.Visible;
            // The earbud EncoDeck drives itself is read over its own control channel, which needs
            // no Windows A2DP link; every other earbud goes through the upstream CLI, which
            // needs the earbud to be connected to Windows.
            if (_hiRes is not null && _hiRes.CanRead(device, connected))
            {
                try
                {
                    var state = await _hiRes.ReadAsync(device, waitForDevice);
                    if (state is null)
                    {
                        // The earbud did not answer on its control channel; the state stays unknown.
                        _hiResEnabled = null;
                        _hiResUnsupported = false;
                    }
                    else
                    {
                        _hiResEnabled = state.Enabled;
                        _hiResUnsupported = !state.Supported;
                    }
                }
                catch (Exception)
                {
                    _hiResEnabled = null;
                    _hiResUnsupported = false;
                }
            }
        }
        // An earbud with Hi-Res on advertises more than the negotiated capability set describes.
        _capabilitiesStale = _hiResEnabled == true && _maxKbps <= 400;
        if (_capabilitiesStale && !_negotiated)
        {
            // The driver cached the capabilities from before the switch was turned on: let it
            // negotiate again so the lists show what the earbud can really do.
            _negotiated = true;
            Status.Text = Loc.Get("Lhdc_Negotiating");
            if (await RefreshCapabilitiesAsync(device, request: true))
                await ReadCapabilitiesAsync(device, await backend.QueryAsync(device.Instance, "audio-status"));
        }
        RebuildChoices();
        UpdateHiRes();
        // The observed line is rebuilt whenever the live bitrate changes, so everything that does
        // not change with it is kept in its own part.
        _observedHead = Loc.Format("Lhdc_Observed", mode);
        _observedTail = _maxKbps > 0 ? " · " + Loc.Format("Lhdc_NegotiatedLimit", _maxKbps) : "";
        // A driver that has not negotiated with the earbud has no limit to show: say so instead
        // of letting the missing limit look like a negotiated one.
        if (_capabilitiesMissing) _observedTail += " · " + Loc.Get("Lhdc_CapabilitiesNotNegotiated");
        // A stale set only exists when there is a set at all.
        if (_capabilitiesStale && !_capabilitiesMissing) _observedTail += " · " + Loc.Get("Lhdc_NegotiatedStale");
        if (!found) _observedTail += " · " + Loc.Get("Lhdc_DeviceMissing");
        _activeKbps = active;
        RenderObserved();
    }

    /// <summary>
    /// Rebuilds the observed line from its parts: the mode, the live bitrate and the notes that
    /// belong to the current negotiation.
    /// </summary>
    private void RenderObserved()
    {
        if (_observedHead.Length == 0) return;
        Observed.Text = _observedHead + (_activeKbps > 0 ? $" · {_activeKbps} kbps" : "") + _observedTail;
    }

    /// <summary>
    /// Follows the bitrate the LHDC service is really playing. The service publishes it when the
    /// stream starts and whenever the adaptive control changes it, so the observed line shows the
    /// current bitrate while the earbuds play.
    /// </summary>
    private async void OnLiveTick(DispatcherQueueTimer sender, object args)
    {
        if (_busy || _liveQuery || !LhdcCompanion.IsReady) return;
        if (Devices.SelectedItem is not LhdcDevice device) return;
        _liveQuery = true;
        try
        {
            var audio = await LhdcCompanion.Backend.QueryAsync(device.Instance, "audio-status");
            int active = audio["active_kbps"]?.GetValue<int>() ?? 0;
            if (active == _activeKbps) return;
            _activeKbps = active;
            RenderObserved();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _liveQuery = false; }
    }

    /// <summary>
    /// Reads what the driver negotiated with the earbud. The service publishes the capability set
    /// it negotiated, so the lists are built from exactly what the encoder will be allowed to use.
    /// </summary>
    private async Task ReadCapabilitiesAsync(LhdcDevice device, JsonObject audio)
    {
        var backend = LhdcCompanion.Backend;
        _capabilities = audio["peer_capabilities"]?.GetValue<string>() ?? "";
        _mediaMtu = audio["media_mtu"]?.GetValue<int>() ?? 0;
        _maxKbps = 0;
        _allowedRates = [];
        _allowedDepths = [];
        _bitratesByRate = [];
        // Without a capability set the driver has not negotiated with the earbud yet: the lists
        // that are built from it are the fallback table, not the earbud's own capabilities.
        _capabilitiesMissing = _capabilities.Length == 0;
        if (_capabilities.Length == 0) return;
        // The media MTU narrows the result: the encoder cannot split a frame, so a single frame
        // that does not fit into the negotiated payload is not offered at all. Without a known
        // MTU only the earbud's own ceiling applies.
        var options = await backend.ProfileOptionsAsync(device.Instance, _capabilities, _mediaMtu);
        var rates = options["rates"]?.AsArray();
        _allowedRates = rates?.Select(r => r!["sample_rate"]!.GetValue<int>()).ToArray() ?? [];
        _allowedDepths = options["bits"]?.AsArray().Select(b => b!.GetValue<int>()).ToArray() ?? [];
        _bitratesByRate = rates?.ToDictionary(
            r => r!["sample_rate"]!.GetValue<int>(),
            r => r!["bitrates"]?.AsArray().Select(b => b!.GetValue<int>()).ToArray() ?? []) ?? [];
        _maxKbps = options["max_kbps"]?.GetValue<int>() ?? 0;
    }

    private void UpdateHiRes()
    {
        _updating = true;
        try { HiRes.IsOn = _hiResEnabled == true; }
        finally { _updating = false; }
        HiResHint.Text = _hiResEnabled switch
        {
            true => Loc.Get("Lhdc_HiResOn"),
            false => Loc.Get("Lhdc_HiResOff"),
            // An earbud the driver has not negotiated with yet is a different case than one that
            // is simply not connected: the first one waits for a session, the second one for the
            // earbud.
            _ => _hiResUnsupported ? Loc.Get("Lhdc_HiResUnsupported")
                : _capabilitiesMissing ? Loc.Get("Lhdc_HiResNotNegotiated") : Loc.Get("Lhdc_HiResUnknown"),
        };
    }

    /// <summary>
    /// Rebuilds the format lists from what the earbud advertises. The lists are the earbud's own
    /// capability table, so a bitrate the earbud will not accept is never offered.
    /// </summary>
    private void RebuildChoices()
    {
        _updating = true;
        try
        {
            int[] rates = _allowedRates.Length > 0 ? _allowedRates : FallbackRates();
            if (!rates.Contains(_rate)) _rate = rates.Contains(48000) ? 48000 : rates[0];
            var rateItems = rates.Select(r => new Choice(r, $"{r / 1000.0:0.#} kHz")).ToArray();
            Rate.ItemsSource = rateItems;
            Rate.SelectedItem = rateItems.FirstOrDefault(c => c.Value == _rate);

            int[] depths = _allowedDepths.Length > 0 ? _allowedDepths : [16, 24];
            if (!depths.Contains(_bits)) _bits = depths.Contains(24) ? 24 : depths[0];
            var depthItems = depths.Select(d => new Choice(d, $"{d} bit")).ToArray();
            Depth.ItemsSource = depthItems;
            Depth.SelectedItem = depthItems.FirstOrDefault(c => c.Value == _bits);

            int[] bitrates = AllowedBitrates(_rate);
            int selected = SelectBitrate(_rate, bitrates, _preferredCanonical);
            var bitrateItems = bitrates.Select(k => new Choice(k, $"{k} kbps")).ToArray();
            Bitrate.ItemsSource = bitrateItems;
            Bitrate.SelectedItem = bitrateItems.FirstOrDefault(c => c.Value == selected);
        }
        finally { _updating = false; }
    }

    /// <summary>
    /// The rates to offer when the driver has not negotiated anything yet. Without a negotiated
    /// capability set only the earbud's Hi-Res state is known, so the choice stays inside what
    /// LHDC V5 guarantees unless the earbud has its Hi-Res switch on.
    /// </summary>
    private int[] FallbackRates() => _hiResEnabled == true ? [44100, 48000, 96000, 192000] : [44100, 48000];

    /// <summary>
    /// The bitrates to offer for a sample rate. With a negotiated capability set this is the
    /// earbud's own table; without one it is the LHDC V5 range, widened when the earbud has its
    /// Hi-Res switch on. 44.1 kHz tops out at 900 kbps because its frames no longer fit the
    /// media payload at 1000 kbps.
    /// </summary>
    private int[] AllowedBitrates(int rate)
    {
        if (_bitratesByRate.TryGetValue(rate, out var allowed) && allowed.Length > 0) return allowed;
        int[] canonical = _hiResEnabled == true
            ? rate == 44100 ? [64, 160, 192, 256, 320, 400, 500, 900] : [64, 160, 192, 256, 320, 400, 500, 900, 1000]
            : [64, 160, 192, 256, 320, 400];
        return canonical.Select(k => LhdcBitrates.ToActual(rate, k)).ToArray();
    }

    /// <summary>
    /// Picks the bitrate that matches the canonical choice. 44.1 kHz spells 240 and 480 where the
    /// other rates use 256 and 500, so both forms are compared on their canonical value.
    /// </summary>
    private static int SelectBitrate(int rate, int[] allowed, int canonical)
    {
        if (allowed.Length == 0) return 400;
        foreach (int kbps in allowed)
            if (LhdcBitrates.ToCanonical(rate, kbps) == canonical) return kbps;
        int? lower = null;
        foreach (int kbps in allowed)
        {
            int value = LhdcBitrates.ToCanonical(rate, kbps);
            if (value > canonical) continue;
            if (lower is null || value > LhdcBitrates.ToCanonical(rate, lower.Value)) lower = kbps;
        }
        if (lower is not null) return lower.Value;
        // The allowed list is never empty here, so this always finds a value.
        return allowed.MinBy(k => LhdcBitrates.ToCanonical(rate, k));
    }

    /// <summary>A string field of a backend result; null when it is missing or not a string.</summary>
    private static string? Text(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private async void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing)
        {
            _negotiated = false;
            await Operate(() => ReadStatus());
        }
        Sync();
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Operate(RefreshDevices);
    private async void OnApply(object sender, RoutedEventArgs e) => await ChangeMode(true);
    private async void OnRestore(object sender, RoutedEventArgs e) => await ChangeMode(false);

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (Rate.SelectedItem is not Choice choice) return;
        int canonical = LhdcBitrates.ToCanonical(_rate, (Bitrate.SelectedItem as Choice)?.Value ?? 400);
        _rate = choice.Value;
        _preferredCanonical = canonical;
        RebuildChoices();
    }

    private void OnBitrateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (Bitrate.SelectedItem is Choice choice) _preferredCanonical = LhdcBitrates.ToCanonical(_rate, choice.Value);
    }

    /// <summary>
    /// Turns the earbud's Hi-Res switch on or off. The switch decides what the earbud advertises
    /// to Windows, so nothing above 400 kbps can be applied while it is off.
    /// </summary>
    private async void OnHiResToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool desired = HiRes.IsOn == true;
        // The switch only moves once the earbud confirmed the new state.
        _updating = true;
        try { HiRes.IsOn = _hiResEnabled == true; }
        finally { _updating = false; }
        if (Devices.SelectedItem is not LhdcDevice device || _hiRes is null) return;
        await Operate(async () =>
        {
            var installed = LhdcBackend.InstalledProfile();
            if (!desired && installed is not null && (installed.SampleRate > 48000 || installed.Kbps > 400))
                throw new InvalidOperationException(Loc.Get("Lhdc_HiResLowerFirst"));
            Status.Text = Loc.Get("Lhdc_HiResBusy");
            var state = await _hiRes.SetAsync(device, desired);
            _hiResEnabled = state.Enabled;
            _hiResUnsupported = !state.Supported;
            UpdateHiRes();
            // The driver caches what the earbud advertised, so it has to negotiate again. The
            // cache is dropped whether or not the switch itself changed, so the new limits show
            // up without a manual refresh.
            try
            {
                LhdcBackend.RequestCapabilityRefresh();
                _negotiated = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // LHDC is not installed, so there is nothing to renegotiate.
            }
            bool renegotiated = await RefreshCapabilitiesAsync(device, request: false);
            await ReadStatus(waitForDevice: true);
            Status.Text = !renegotiated ? Loc.Get("Lhdc_HiResPending")
                : desired ? Loc.Get("Lhdc_HiResOn") : Loc.Get("Lhdc_HiResOff");
        });
    }

    /// <summary>
    /// Asks the driver to negotiate the earbud's capabilities again and waits for the new set.
    /// The driver caches what the earbud advertised, so a changed Hi-Res switch only shows up
    /// after the next negotiation. Returns false when the driver did not publish a new set.
    /// </summary>
    private async Task<bool> RefreshCapabilitiesAsync(LhdcDevice device, bool request)
    {
        var backend = LhdcCompanion.Backend;
        if (request)
        {
            // A failure here is reported to the user instead of being hidden behind a timeout.
            LhdcBackend.RequestCapabilityRefresh();
            _negotiated = true;
        }
        string previous = _capabilities;
        // The driver renegotiates when its session restarts, which the switch itself triggers; a
        // running stream can take a moment to give the session up.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var audio = await backend.QueryAsync(device.Instance, "audio-status");
            string caps = audio["peer_capabilities"]?.GetValue<string>() ?? "";
            if (caps.Length > 0 && caps != previous) return true;
        }
        return false;
    }

    private async Task ChangeMode(bool lhdc) => await Operate(async () =>
    {
        if (Devices.SelectedItem is not LhdcDevice device) return;
        int rate = (Rate.SelectedItem as Choice)?.Value ?? 48000;
        int kbps = (Bitrate.SelectedItem as Choice)?.Value ?? 400;
        var profile = new LhdcProfile(rate, (Depth.SelectedItem as Choice)?.Value ?? 24, kbps, Adaptive.IsChecked == true);
        Status.Text = Loc.Get("Lhdc_Applying");
        var backend = LhdcCompanion.Backend;
        // Local format validation first; the deployment probes the selected earbud's actual capabilities.
        if (lhdc)
        {
            if (_capabilities.Length > 0 && !_capabilitiesStale)
            {
                try { await backend.ConfigureAsync(device.Instance, _capabilities, profile); }
                catch (IOException)
                {
                    // The cached capabilities may be older than the earbud's current state: read them again.
                    var audio = await backend.QueryAsync(device.Instance, "audio-status");
                    string fresh = audio["peer_capabilities"]?.GetValue<string>() ?? "";
                    if (fresh.Length == 0 || fresh == _capabilities) throw;
                    await backend.ConfigureAsync(device.Instance, fresh, profile);
                }
            }
            else
            {
                // The driver has not negotiated the earbud's capabilities (yet). The deployment
                // probes the earbud itself, so only the encoder's own table is checked here.
                await backend.QueryAsync(device.Instance, "profile-check", $"{profile.SampleRate}", $"{profile.Bits}", $"{profile.Kbps}");
            }
        }
        // The deployment runs for a while: the script reports the stage it is in, so the card
        // shows where the wait is instead of only saying that it is waiting.
        _deploying = true;
        var progress = new Progress<LhdcDeploymentProgress>(p =>
        {
            if (!_deploying) return;
            DeployProgressPanel.Visibility = Visibility.Visible;
            DeployStage.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage
                : $"{p.Detail}（{p.Elapsed.TotalSeconds:F0}s）";
        });
        try
        {
            await backend.ApplyAsync(device.Instance, lhdc, profile, progress);
        }
        finally
        {
            _deploying = false;
            DeployProgressPanel.Visibility = Visibility.Collapsed;
        }
        _capabilitiesStale = false;
        CardNotice.Show(Notice, InfoBarSeverity.Success, Loc.Get("Lhdc_Applied"));
        await ReadStatus();
    });

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        Status.Text = Loc.Get("Component_Checking");
        var outcome = await LhdcCompanion.CheckForUpdateAsync(App.Instance.Host.Settings.Current.IncludePrereleaseUpdates, force: true);
        CardNotice.Show(Notice, outcome is ComponentUpdateOutcome.Available or ComponentUpdateOutcome.UpToDate ? InfoBarSeverity.Success : InfoBarSeverity.Warning, outcome switch
        {
            ComponentUpdateOutcome.Available => Loc.Format("Component_UpdateFound", LhdcCompanion.UpdateTarget?.Version ?? LhdcCompanion.OnlineRelease?.Version),
            ComponentUpdateOutcome.UpToDate => Loc.Get("Component_UpToDate"),
            ComponentUpdateOutcome.RateLimited => Loc.Get("Component_RateLimited"),
            ComponentUpdateOutcome.Offline => Loc.Get("Component_Offline"),
            _ => Loc.Get("Component_CheckFailed"),
        });
    });

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Lhdc_UninstallTitle"),
            Content = new TextBlock { Text = Loc.Get("Lhdc_UninstallBody"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("Component_Uninstall"),
            CloseButtonText = Loc.Get("Lhdc_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await CardNotice.ShowDialogAsync(dialog, XamlRoot) != ContentDialogResult.Primary) return;
        await Operate(async () =>
        {
            await LhdcCompanion.UninstallAsync();
            Devices.ItemsSource = null;
            Observed.Text = "";
            CardNotice.Show(Notice, InfoBarSeverity.Success, Loc.Get("Lhdc_Uninstalled"));
        });
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _download?.Cancel();
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (!LhdcCompanion.CanDownload) return;
        await Operate(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _download = cancellation;
            Progress.Visibility = Cancel.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            Status.Text = Loc.Get("Lhdc_Downloading");
            Sync();
            try
            {
                var progress = new Progress<UpdateDownloadProgress>(p =>
                {
                    if (_download != cancellation) return;
                    Progress.IsIndeterminate = false;
                    Progress.Value = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
                    Status.Text = Loc.Format("Lhdc_DownloadProgress", p.Received / 1048576.0, p.Total / 1048576.0);
                });
                await LhdcCompanion.DownloadAsync(progress, cancellation.Token);
                await RefreshDevices();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                CardNotice.Show(Notice, InfoBarSeverity.Informational, Loc.Get("Lhdc_Cancelled"));
            }
            finally { _download = null; Progress.Visibility = Cancel.Visibility = Visibility.Collapsed; }
        });
    }
}
