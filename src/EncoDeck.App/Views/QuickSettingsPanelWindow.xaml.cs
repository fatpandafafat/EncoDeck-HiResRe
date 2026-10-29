// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.App.Controls;
using EncoDeck.App.Services;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EncoDeck.App.Views;

/// <summary>
/// The device panel on top of Windows 11 Quick Settings: lined up with its cards, coming and going
/// with it. It never takes the focus, because Quick Settings closes as soon as it loses the
/// foreground; clicks still reach it.
/// </summary>
public sealed partial class QuickSettingsPanelWindow : Window
{
    /// <summary>Margin the shell keeps around its cards (DIP); Quick Settings' window region includes it.</summary>
    private const double CardMargin = 12;
    private const double SlideDip = 16;
    private const double EnterMilliseconds = 300;
    private const double MoveMilliseconds = 250;
    private const double ExitMilliseconds = 160;

    private EventHandler<object>? _frame;
    /// <summary>What the panel shows on screen (physical pixels); the window rectangle is larger by <see cref="_invisible"/>.</summary>
    private PixelRect _target;
    private WindowInterop.FrameInsets _invisible;
    private double _y;
    private double _alpha;
    private int _slide;
    private bool _visible;
    private bool _leaving;
    private bool _closed;
    private bool _loading;
    private PixelRect? _pendingCards;

    public QuickSettingsPanelWindow()
    {
        InitializeComponent();
        Loc.ApplyLanguage(this);
        SystemBackdrop = new AlwaysActiveAcrylicBackdrop();
        WindowInterop.MakeFlyout(this, noActivate: true);
        // Layered, so the window's own alpha does the fades (fading the content would leave the backdrop).
        WindowInterop.MakeLayered(this);
        WindowInterop.SetWindowAlpha(this, 0);
        ContentPanel.OpenAppRequested += (_, _) => App.Instance.Windows.ShowMain();
        Closed += (_, _) =>
        {
            // Nothing may touch a closed window again (WinUI throws for it).
            _closed = true;
            StopFrames();
        };
    }

    /// <summary>True from the moment the panel starts coming in until it has gone.</summary>
    public bool IsShown => _visible;

    /// <summary>Raised when the panel has gone (hidden, not closed).</summary>
    public event EventHandler? Hidden;

    /// <summary>
    /// Shows the panel above <paramref name="cards"/> (Quick Settings' window region, physical
    /// pixels), or glides it there when it is already shown. Leaves when it does not fit.
    /// </summary>
    public void Track(PixelRect cards)
    {
        if (_closed) return;
        if (!ContentPanel.IsLoaded)
        {
            LoadThenTrack(cards);
            return;
        }

        var monitor = ShellEnvironment.GetMonitorAt(new PixelPoint(cards.X + cards.Width / 2, cards.Bottom - 1));
        double scale = monitor.Scale;
        int margin = (int)Math.Round(CardMargin * scale);
        int width = PopupPlacement.QuickSettingsPanelWidth(cards, margin);
        if (width <= 0)
        {
            Leave();
            return;
        }
        // The target is what shows of the window: it is as wide as the cards, its invisible resize
        // borders stick out around them and its thin border takes a little of the content's room.
        (_invisible, var border) = WindowInterop.GetFrame(this);
        ContentPanel.Measure(new Windows.Foundation.Size(Math.Max(1, width - border.Horizontal) / scale, double.PositiveInfinity));
        int height = (int)Math.Ceiling(ContentPanel.DesiredSize.Height * scale) + border.Vertical;
        if (PopupPlacement.PlaceAboveQuickSettings(cards, monitor.WorkArea, height, margin) is not { } target)
        {
            Leave();
            return;
        }
        _slide = (int)Math.Round(SlideDip * scale);

        if (!_visible)
        {
            Enter(target);
            return;
        }
        // Other topmost windows (and the shell, when the Start menu was open) can end up above
        // the panel: keep it at the top of the topmost band while Quick Settings is open.
        _ = WindowInterop.EnsureTopmost(this);
        if (target == _target && !_leaving)
        {
            // Quick Settings keeps its width while it is open, but the window can still be at the
            // width of a previous opening (a hidden window shown again) or one Windows adjusted.
            // Put the computed size back so the panel always spans the cards.
            MatchWindow(target);
            return;
        }

        // Sub-page, media card or content change: glide to the new place (and come back if leaving).
        bool resized = target.X != _target.X || target.Width != _target.Width || target.Height != _target.Height;
        _target = target;
        _leaving = false;
        if (resized) WindowInterop.MoveAndResize(this, WindowRect(target));
        if (!ShellEnvironment.AnimationsEnabled())
        {
            Apply(target.Y, 1);
            return;
        }
        Animate(target.Y, 1, _alpha < 1 ? EnterMilliseconds * (1 - _alpha) : MoveMilliseconds, EaseOutCubic, completed: null);
    }

    /// <summary>Fades out towards the cards (Quick Settings is closing), then hides.</summary>
    public void Leave()
    {
        if (_loading) _pendingCards = null;
        if (!_visible || _leaving || _closed) return;
        _leaving = true;
        if (!ShellEnvironment.AnimationsEnabled())
        {
            HideNow();
            return;
        }
        Animate(_target.Y + _slide, 0, ExitMilliseconds * Math.Max(0.35, _alpha), EaseInCubic, HideNow);
    }

    /// <summary>
    /// The first time, the content has never been laid out, so its height is unknown: lay it out in
    /// the (fully transparent) window first, then come in at the right size.
    /// </summary>
    private void LoadThenTrack(PixelRect cards)
    {
        _pendingCards = cards;
        if (_loading) return;
        _loading = true;
        WindowInterop.SetWindowAlpha(this, 0);
        // Off every screen, so the invisible window cannot catch a click meanwhile.
        WindowInterop.MoveAndResize(this, new PixelRect(-24000, -22000, cards.Width, cards.Width));
        ContentPanel.Loaded += OnFirstLoaded;
        WindowInterop.GetAppWindow(this).Show(activateWindow: false);

        void OnFirstLoaded(object sender, RoutedEventArgs e)
        {
            ContentPanel.Loaded -= OnFirstLoaded;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _loading = false;
                if (_closed) return;
                if (_pendingCards is { } pending) Track(pending);
                else if (!_visible) WindowInterop.GetAppWindow(this).Hide();
            });
        }
    }

    private void Enter(PixelRect target)
    {
        _target = target;
        _visible = true;
        _leaving = false;
        bool animate = ShellEnvironment.AnimationsEnabled();
        _y = animate ? target.Y + _slide : target.Y;
        _alpha = animate ? 0 : 1;
        WindowInterop.MoveAndResize(this, WindowRect(target));
        WindowInterop.SetWindowAlpha(this, (byte)Math.Round(255 * _alpha));
        WindowInterop.GetAppWindow(this).Show(activateWindow: false);
        _ = WindowInterop.EnsureTopmost(this);
        // A window that was hidden can come back at the size it had before the resize above; the
        // first visible frame should already be as wide as Quick Settings.
        MatchWindow(target);
        if (animate) Animate(target.Y, 1, EnterMilliseconds, EaseOutCubic, completed: null);
    }

    /// <summary>
    /// Re-applies the horizontal position and the size (never Y, which the slide animation owns)
    /// when the window does not really have them, so the panel follows the cards' width even when a
    /// resize made while it was hidden was dropped or the system adjusted the window.
    /// </summary>
    private void MatchWindow(PixelRect target)
    {
        if (_closed) return;
        var actual = WindowInterop.GetWindowBounds(this);
        var wanted = WindowRect(target);
        if (Math.Abs(actual.X - wanted.X) <= 1 && Math.Abs(actual.Width - wanted.Width) <= 1 && Math.Abs(actual.Height - wanted.Height) <= 1) return;
        WindowInterop.MoveAndResize(this, wanted);
    }

    /// <summary>The window rectangle that shows <paramref name="visible"/>, at the animated Y.</summary>
    private PixelRect WindowRect(PixelRect visible) => _invisible.Around(visible with { Y = (int)Math.Round(_y) });

    private void HideNow()
    {
        StopFrames();
        if (_closed) return;
        _visible = false;
        _leaving = false;
        WindowInterop.SetWindowAlpha(this, 0);
        WindowInterop.GetAppWindow(this).Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the window (without resizing it) and fades its alpha from where it is now, once per
    /// displayed frame (<see cref="CompositionTarget.Rendering"/>), so the motion is as smooth as the
    /// display. A new animation continues from wherever the previous one was.
    /// </summary>
    private void Animate(double toY, double toAlpha, double milliseconds, Func<double, double> ease, Action? completed)
    {
        StopFrames();
        double fromY = _y, fromAlpha = _alpha;
        double duration = Math.Max(1, milliseconds);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _frame = (_, _) =>
        {
            if (_closed)
            {
                StopFrames();
                return;
            }
            double p = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration, 0, 1);
            double eased = ease(p);
            Apply(fromY + (toY - fromY) * eased, fromAlpha + (toAlpha - fromAlpha) * eased);
            if (p < 1) return;
            StopFrames();
            completed?.Invoke();
        };
        CompositionTarget.Rendering += _frame;
    }

    private void Apply(double y, double alpha)
    {
        _y = y;
        _alpha = Math.Clamp(alpha, 0, 1);
        WindowInterop.MoveTo(this, _target.X - _invisible.Left, (int)Math.Round(y) - _invisible.Top);
        WindowInterop.SetWindowAlpha(this, (byte)Math.Round(255 * _alpha));
    }

    private void StopFrames()
    {
        if (_frame is null) return;
        CompositionTarget.Rendering -= _frame;
        _frame = null;
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseInCubic(double t) => t * t * t;
}
