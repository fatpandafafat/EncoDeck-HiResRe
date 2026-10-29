// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace EncoDeck.App.Views;

/// <summary>
/// Quick panel shown from the notification area. It is activated so that keyboard and screen
/// readers work (the user just clicked the taskbar, so no app loses focus), and hides as soon as
/// it is deactivated, i.e. when the user clicks anywhere else.
/// </summary>
public sealed partial class TrayPanelWindow : Window
{
    public const double PanelWidth = 360;
    private const double Margin = 12;

    private PixelRect? _anchor;
    private bool _visible;

    public TrayPanelWindow()
    {
        InitializeComponent();
        Loc.ApplyLanguage(this);
        WindowInterop.MakeFlyout(this, noActivate: false);
        Activated += OnActivated;
        ContentPanel.OpenAppRequested += OnOpenApp;
        ContentPanel.SizeChanged += (_, _) => { if (_visible) Place(); };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    public bool IsShown => _visible;

    /// <summary>Raised when the panel hides (deactivated, Escape, or an action that opens something else).</summary>
    public event EventHandler? Hidden;

    /// <summary>When the panel last hid; used to ignore the tray click that caused the hide.</summary>
    public DateTimeOffset LastHiddenAt { get; private set; }

    public void ShowNear(PixelRect? trayIconRect)
    {
        _anchor = trayIconRect;
        _visible = true;
        Place();
        WindowInterop.GetAppWindow(this).Show(activateWindow: true);
        Activate();
        // The tray click gives the shell the foreground, not this window: take it explicitly so the
        // first click inside the panel already acts, and clicking elsewhere hides it again.
        WindowInterop.BringToForeground(this);
        ContentPanel.Focus(FocusState.Programmatic);
        // Activation can complete after this call returns; focus the content once more when it has.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_visible) ContentPanel.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Diagnostics (<see cref="Services.UiSnapshot"/>): shown at <paramref name="origin"/> without activation.</summary>
    internal void ShowForDiagnostics(PixelPoint origin, double scale)
    {
        ContentPanel.Measure(new Windows.Foundation.Size(PanelWidth, double.PositiveInfinity));
        double heightDip = Math.Max(160, ContentPanel.DesiredSize.Height);
        WindowInterop.MoveAndResize(this, new PixelRect(origin.X, origin.Y, (int)Math.Ceiling(PanelWidth * scale), (int)Math.Ceiling(heightDip * scale)));
        WindowInterop.GetAppWindow(this).Show(activateWindow: false);
    }

    public void HidePanel()
    {
        if (!_visible) return;
        _visible = false;
        LastHiddenAt = DateTimeOffset.UtcNow;
        WindowInterop.GetAppWindow(this).Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private void Place()
    {
        var anchorPoint = _anchor is { } a ? new PixelPoint(a.X + a.Width / 2, a.Y + a.Height / 2) : ShellEnvironment.GetCursorPosition();
        var monitor = ShellEnvironment.GetMonitorAt(anchorPoint);
        double scale = monitor.Scale;

        // Measure the content at the panel width to size the window to it.
        ContentPanel.Measure(new Windows.Foundation.Size(PanelWidth, double.PositiveInfinity));
        double heightDip = Math.Max(160, ContentPanel.DesiredSize.Height);

        var edge = monitor.TaskbarEdge ?? ShellEnvironment.GetPrimaryTaskbarMonitor().TaskbarEdge ?? TaskbarEdge.Bottom;
        var rect = PopupPlacement.PlaceNearTaskbar(
            monitor.WorkArea,
            edge,
            anchorPoint,
            (int)Math.Ceiling(PanelWidth * scale),
            (int)Math.Ceiling(heightDip * scale),
            (int)(Margin * scale));
        WindowInterop.MoveAndResize(this, rect);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) HidePanel();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private void OnOpenApp(object? sender, EventArgs e)
    {
        HidePanel();
        App.Instance.Windows.ShowMain();
    }
}
