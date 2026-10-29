// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using System.Globalization;
using EncoDeck.App.Controls;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using EncoDeck.Platform.Media;
using EncoDeck.Platform.Resources;
using EncoDeck.Platform.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace EncoDeck.App.Views;

/// <summary>
/// Transient "earbuds connected" pop-up near the taskbar.
/// <para>
/// With an official HeyMelody pack it is HeyMelody's connection dialog: the pack's video segment
/// (decoded silently by <see cref="SilentVideoPresenter"/>: no audio, no media session), the battery
/// levels, pictures and title where and when the pack's config puts them (<see cref="PopupLayout"/>,
/// <see cref="PopupTimeline"/>); otherwise a compact card.
/// </para>
/// <para>
/// It never takes focus (WS_EX_NOACTIVATE). While a fullscreen app or game is in front it is
/// click-through: the system routes every mouse event to the window underneath. The state is
/// re-checked while the pop-up is visible, so switching into a game makes it click-through at
/// once; exclusive fullscreen or a failed click-through check closes it immediately.
/// </para>
/// </summary>
public sealed partial class ConnectionPopupWindow : Window
{
    public const double CardWidth = 380;
    public const double CardHeight = 136;
    /// <summary>Same aspect as HeyMelody's connection video (960 × 1040).</summary>
    public const double VideoWidth = 340;
    public const double VideoHeight = 368;
    private const double Margin = 12;
    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(250);
    private const double EnterMilliseconds = 420;
    private const double ExitMilliseconds = 320;
    private const double SlideDip = 24;

    private readonly TimeSpan _fallbackDuration;
    private readonly bool _animate;
    private PopupTimeline? _timeline;
    private PopupLayout _layout = PopupLayout.Default;
    private string? _videoPath;
    private string? _packRoot;
    private SilentVideoPresenter? _video;
    private PopupBatteryArrangement? _arrangement;
    private DeviceModelView? _foreground;
    private DispatcherQueueTimer? _closeTimer;
    private readonly List<DispatcherQueueTimer> _stepTimers = [];
    private DispatcherQueueTimer? _guardTimer;
    private EventHandler<object>? _frame;
    private PixelRect _bounds;
    private int _slideDistance;
    private bool _slideFromTop;
    private bool _passThrough;
    private bool _hovered;
    private bool _closing;
    private bool _finished;
    private bool _huawei;
    private bool _headphones;
    private bool _huaweiPick;
    private double _huaweiHeight;
    private double _huaweiTextDelay;

    /// <param name="fallbackDuration">How long to show when the media has no official timeline.</param>
    /// <param name="passThrough">Start click-through (a fullscreen app or game is in front).</param>
    public ConnectionPopupWindow(TimeSpan fallbackDuration, bool passThrough)
    {
        _fallbackDuration = fallbackDuration;
        _animate = ShellEnvironment.AnimationsEnabled();
        _passThrough = passThrough;
        InitializeComponent();
        Loc.ApplyLanguage(this);
        // A Window's x:Bind bindings start on Activated, which never fires for a non-activating
        // pop-up: initialise them here, or every bound value (name, levels) stays empty.
        Bindings.Initialize();
        WindowInterop.MakeFlyout(this, noActivate: true, border: false);
        // Layered from the start, so click-through can be switched on at any moment; the window's
        // own alpha also does the fades (fading the content would expose the window background).
        WindowInterop.MakeLayered(this);
        if (_animate) WindowInterop.SetWindowAlpha(this, 0);
        if (passThrough) WindowInterop.SetClickThrough(this, true);
        Closed += (_, _) =>
        {
            // Nothing may touch a closed window again (WinUI throws for it).
            _closing = true;
            _finished = true;
            Release();
        };
    }

    public DeviceViewModel ViewModel => App.Instance.Device;

    public string AnnouncementText => Loc.Format("Popup_Announcement", ViewModel.DeviceName);

    /// <summary>True while the pop-up lets all mouse input through.</summary>
    public bool IsClickThrough => _passThrough;

    /// <summary>The official HeyMelody timeline in use, or null for the compact card.</summary>
    public PopupTimeline? Timeline => _timeline;

    /// <summary>The official layout in use (meaningful when <see cref="Timeline"/> is set).</summary>
    public PopupLayout Layout => _layout;

    /// <summary>Diagnostics: show this pop-up media (a pack video or image) instead of the device's.</summary>
    internal string? MediaOverride { get; set; }

    /// <summary>Diagnostics: decoder, frames shown and what the battery groups show.</summary>
    internal string DiagnosticState =>
        $"video={_videoPath is not null} pipeline={_video?.Player?.Pipeline} size={_video?.Player?.Width}x{_video?.Player?.Height} " +
        $"frames={_video?.Player?.FramesPublished} last={_video?.Player?.LastFrameTime?.TotalMilliseconds} holding={_video?.Player?.IsHolding} " +
        $"headphones={_headphones} headphoneMedia=[{HeadphonePicture.DiagnosticState}] huawei={_huawei} pick={_huaweiPick} huaweiMedia=[{HuaweiPicture.DiagnosticState}] style={_layout.BatteryStyle} videoVisible={_arrangement?.VideoVisible} images={_arrangement?.ImagesVisible} foreground=[{_foreground?.DiagnosticState ?? "-"}] " +
        $"groups=[{string.Join("; ", _arrangement?.Groups.Select(g => $"{g.Group}:{g.Image}:{string.Join(",", g.Slots.Select(x => $"{x.Slot}={x.Battery}"))}") ?? [])}] " +
        $"column=[{string.Join(",", _arrangement?.Column ?? [])}] title={(TitleText.Visibility == Visibility.Visible ? TitleText.Text : "-")} shown={_shown:0.##} dialog={Scaled.Height:0.#} compact={Picture.DiagnosticState} topmost={WindowInterop.IsTopmost(this)} bounds={_bounds}";

    /// <summary>Diagnostics: switch to click-through as a fullscreen app would.</summary>
    internal void ForceClickThroughForDiagnostics() => EnterClickThrough();

    /// <summary>Diagnostics: keep the guard from reacting to the foreground while a check runs.</summary>
    internal bool SuspendGuardForDiagnostics { get; set; }

    /// <summary>Screen centre of the pop-up (physical pixels).</summary>
    public PixelPoint Center => new(_bounds.X + _bounds.Width / 2, _bounds.Y + _bounds.Height / 2);

    public event EventHandler? Dismissed;

    /// <param name="placement">Explicit bounds in physical pixels (diagnostics); default: the user's position and size.</param>
    public void ShowPopup(PixelRect? placement = null)
    {
        ChooseLayout();
        var settings = App.Instance.Host.Settings.Current;
        var monitor = ShellEnvironment.GetPrimaryTaskbarMonitor();
        var edge = monitor.TaskbarEdge ?? TaskbarEdge.Bottom;
        double scale = monitor.Scale * settings.PopupScale;
        // The official layout keeps HeyMelody's dp; the Viewbox scales it to the pop-up. A compact
        // layout is as tall as its content, at the same width.
        double dialogHeight = _timeline is null ? PopupLayout.Height : FitOfficialHeight();
        (double width, double height) = _timeline is null ? (CardWidth, CardHeight) : (VideoWidth, VideoWidth * dialogHeight / PopupLayout.Width);
        (Scaled.Width, Scaled.Height) = _timeline is null ? (CardWidth, CardHeight) : (PopupLayout.Width, dialogHeight);
        if (_huawei) { width = CardWidth; height = _huaweiHeight; Scaled.Width = width; Scaled.Height = height; }
        if (_headphones) { width = 320; height = 340; Scaled.Width = width; Scaled.Height = height; }
        int pixelWidth = (int)Math.Ceiling(width * scale);
        int pixelHeight = (int)Math.Ceiling(height * scale);
        // An explicit placement (diagnostics) gives the position; the size is always the layout's.
        _bounds = placement is { } at
            ? new PixelRect(at.X, at.Y, pixelWidth, pixelHeight)
            : PopupPlacement.PlaceAt(settings.PopupPosition, monitor.WorkArea, edge, pixelWidth, pixelHeight, (int)(Margin * monitor.Scale));
        _slideFromTop = PopupPlacement.IsTop(settings.PopupPosition, edge);
        _slideDistance = (int)Math.Round(SlideDip * monitor.Scale);
        WindowInterop.MoveAndResize(this, _animate ? Slid(1) : _bounds);
        _shown = _animate ? 0 : 1;

        WindowInterop.GetAppWindow(this).Show(activateWindow: false);
        _ = WindowInterop.EnsureTopmost(this);
        if (_passThrough) EnterClickThrough();
        if (_closing) return;
        StartVideo((int)Math.Ceiling(VideoHeight * scale));
        if (_animate) EnterWhenFirstFrameShown();
        StartCloseTimer();
        StartReveals();
        StartGuard();
    }

    /// <summary>
    /// The entrance starts with the first video frame (at most 600 ms later), so the pop-up never
    /// slides in empty while the decoder seeks to the segment.
    /// </summary>
    private void EnterWhenFirstFrameShown()
    {
        if (_video is null)
        {
            AnimateWindow(entering: true, completed: null);
            return;
        }
        bool entered = false;
        void Enter()
        {
            if (entered || _closing || _finished) return;
            entered = true;
            AnimateWindow(entering: true, completed: null);
        }
        _video.FirstFrameShown += (_, _) => Enter();
        After(TimeSpan.FromMilliseconds(600), Enter);
    }

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>
    /// Height of the official dialog (dp): the full 347 dp, or for a compact layout (product shots)
    /// what its content needs, with the title placed under the battery row and the video aligned
    /// to the top and cut at the bottom (<see cref="PopupLayout.ContentHeight"/>).
    /// </summary>
    private double FitOfficialHeight()
    {
        double dialogHeight = PopupLayout.Height;
        if (_layout.IsCompact)
        {
            double titleHeight = 0;
            if (TitleText.Visibility == Visibility.Visible)
            {
                // DesiredSize includes the margin: measure the text alone.
                TitleText.Margin = new Thickness(0);
                TitleText.Measure(new Size(PopupLayout.Width - 48, double.PositiveInfinity));
                titleHeight = TitleText.DesiredSize.Height;
            }
            dialogHeight = _layout.ContentHeight(titleHeight);
            TitleText.VerticalAlignment = VerticalAlignment.Top;
            TitleText.Margin = new Thickness(24, PopupLayout.GroupBottom + PopupLayout.CompactTitleGap, 24, 0);
            // Full width, top-aligned: the dialog-sized video is cut at the bottom by the shorter pop-up.
            Video.VerticalAlignment = VerticalAlignment.Top;
            Video.Height = PopupLayout.Height;
            Video.Width = PopupLayout.Width;
        }
        OfficialLayout.Height = dialogHeight;
        OfficialLayout.Clip = new RectangleGeometry { Rect = new Rect(0, 0, PopupLayout.Width, dialogHeight) };
        return dialogHeight;
    }

    private bool IsDark
    {
        get
        {
            var requested = (Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
            return requested == ElementTheme.Dark
                || (requested == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        }
    }

    /// <summary>Video → official layout and timeline (HeyMelody picks its normal/night pack by theme); otherwise the card.</summary>
    private void ChooseLayout()
    {
        ViewModel.PropertyChanged += OnArtworkChanged;
        bool dark = IsDark;
        string? path = MediaOverride ?? (dark ? ViewModel.PopupMediaDarkPath : ViewModel.PopupMediaPath);
        bool isVideo = MediaOverride is not null
            ? string.Equals(Path.GetExtension(MediaOverride), ".mp4", StringComparison.OrdinalIgnoreCase)
            : dark ? ViewModel.PopupMediaDarkIsVideo : ViewModel.PopupMediaIsVideo;

        if (isVideo && path is not null && path.Contains(".reconnect.", StringComparison.OrdinalIgnoreCase) && ConfigureHuawei(path)) return;

        if (ViewModel.IsHeadphones && !isVideo)
        {
            _headphones = true;
            HeadphonePicture.LoopVideo = false;
            HeadphonePicture.FallbackPath = ViewModel.DeviceImagePath;
            HeadphonePicture.SourcePath = path ?? ViewModel.DeviceImagePath;
            HeadphoneCard.Visibility = Visibility.Visible;
            return;
        }

        if (isVideo && path is not null && File.Exists(path) && !path.Contains(".reconnect.", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".alpha-right.mp4", StringComparison.OrdinalIgnoreCase))
        {
            _timeline = PopupTimeline.TryReadFor(path) ?? PopupTimeline.Default with { CloseDelay = _fallbackDuration };
            _layout = PopupLayout.TryReadFor(path) ?? PopupLayout.Default;
            _packRoot = PopupTimeline.FindConfigFor(path) is { } config ? Path.GetDirectoryName(config) : null;
            _videoPath = path;
            OfficialLayout.Visibility = Visibility.Visible;
            Card.Visibility = Visibility.Collapsed;
            ApplyOfficialLayout();
            foreach (var item in new[] { ViewModel.Left, ViewModel.Right, ViewModel.Case }) item.PropertyChanged += OnBatteryChanged;
        }
        else
        {
            _timeline = null;
            ConfigureCompactArtwork(path);
            CompactArt.Loaded += (_, _) => AnimateCompactArtwork(ComponentArt.Visibility == Visibility.Visible);
            Card.Visibility = Visibility.Visible;
            OfficialLayout.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfigureCompactArtwork(string? path)
    {
        Picture.LoopVideo = false;
        Picture.IsVideo = Path.GetExtension(path ?? string.Empty).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        Picture.IsHeadphones = ViewModel.IsHeadphones;
        Picture.FallbackPath = ViewModel.DeviceImagePath;
        Picture.SourcePath = path;
        bool components = ViewModel.DeviceImagePath is null && !Picture.IsVideo && Path.GetExtension(path ?? string.Empty).ToLowerInvariant() is not (".json" or ".webp") && !ViewModel.IsHeadphones
            && ViewModel.LeftArtworkPath is not null && ViewModel.RightArtworkPath is not null && ViewModel.CaseArtworkPath is not null;
        ComponentArt.Visibility = components ? Visibility.Visible : Visibility.Collapsed;
        Picture.Visibility = components ? Visibility.Collapsed : Visibility.Visible;
        LeftArt.SourcePath = ViewModel.LeftArtworkPath;
        RightArt.SourcePath = ViewModel.RightArtworkPath;
        CaseArt.SourcePath = ViewModel.CaseArtworkPath;
    }

    private bool ConfigureHuawei(string path)
    {
        try
        {
            string? config = null;
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            for (int i = 0; directory is not null && i < 5; i++, directory = Path.GetDirectoryName(directory))
            {
                string candidate = Path.Combine(directory, "config.json");
                if (!File.Exists(candidate) || new FileInfo(candidate).Length > 1024 * 1024) continue;
                using var probe = JsonDocument.Parse(File.ReadAllText(candidate));
                if (probe.RootElement.TryGetProperty("huaweiReconnect", out _)) { config = candidate; break; }
            }
            if (config is null) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(config));
            if (!document.RootElement.TryGetProperty("huaweiReconnect", out var layout)) return false;
            double Number(JsonElement item, string key, double fallback, double min, double max)
                => item.TryGetProperty(key, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    && double.IsFinite(number) && number >= min && number <= max ? number : fallback;
            string[] ratio = layout.GetProperty("ratio").GetString()!.Split(':');
            if (ratio.Length != 2 || !double.TryParse(ratio[0], CultureInfo.InvariantCulture, out double rw) || !double.TryParse(ratio[1], CultureInfo.InvariantCulture, out double rh) || rw <= 0 || rh / rw is < .5 or > 2) return false;
            _huaweiHeight = CardWidth * rh / rw;
            _huaweiTextDelay = Number(layout, "firstTextTime", 1000, 0, 10000);
            _huaweiPick = layout.GetProperty("typeId").GetInt32() == 9;
            var param = layout.GetProperty("videoParam");
            double y = Number(param, "reconnectBatteryVerticalBias", .72, 0, 1) * (_huaweiHeight - 56);
            bool merged = ViewModel.Left.HasValue && ViewModel.Right.HasValue && Number(param, "singleBatteryHorizontalBias", .25, 0, 1) != 0 && Math.Abs(ViewModel.Left.Percent - ViewModel.Right.Percent) <= 15;
            HuaweiLeft.Item = merged && ViewModel.Right.Percent < ViewModel.Left.Percent ? ViewModel.Right : ViewModel.Left;
            HuaweiLeft.LabelOverride = merged ? Loc.Get("Battery_Headphones") : null;
            HuaweiRight.Visibility = merged ? Visibility.Collapsed : Visibility.Visible;
            Canvas.SetLeft(HuaweiLeft, Number(param, merged ? "singleBatteryHorizontalBias" : "leftBatteryHorizontalBias", .25, 0, 1) * (CardWidth - 52));
            Canvas.SetLeft(HuaweiRight, Number(param, "rightBatteryHorizontalBias", .4, 0, 1) * (CardWidth - 52));
            Canvas.SetLeft(HuaweiCase, Number(param, "boxBatteryHorizontalBias", .72, 0, 1) * (CardWidth - 52));
            foreach (var element in new FrameworkElement[] { HuaweiLeft, HuaweiRight, HuaweiCase }) Canvas.SetTop(element, y);
            HuaweiTitle.Width = CardWidth - 32;
            Canvas.SetLeft(HuaweiTitle, 16);
            Canvas.SetTop(HuaweiTitle, Number(param, "reconnectTextVerticalBias", .92, 0, 1) * (_huaweiHeight - 30));
            HuaweiLayout.Width = CardWidth; HuaweiLayout.Height = _huaweiHeight;
            HuaweiPicture.LoopVideo = false; HuaweiPicture.IsVideo = true;
            HuaweiPicture.FallbackPath = ViewModel.DeviceImagePath; HuaweiPicture.SourcePath = path;
            HuaweiLevels.Opacity = _animate ? 0 : 1;
            HuaweiLayout.Visibility = Visibility.Visible;
            Card.Visibility = OfficialLayout.Visibility = Visibility.Collapsed;
            _huawei = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or UnauthorizedAccessException) { return false; }
    }

    private void OnArtworkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing || _finished || MediaOverride is not null || (!_headphones && Card.Visibility != Visibility.Visible)) return;
        if (e.PropertyName is not (nameof(DeviceViewModel.DeviceImagePath) or nameof(DeviceViewModel.PopupMediaPath)
            or nameof(DeviceViewModel.PopupMediaDarkPath) or nameof(DeviceViewModel.PopupMediaIsVideo) or nameof(DeviceViewModel.PopupMediaDarkIsVideo)
            or nameof(DeviceViewModel.LeftArtworkPath) or nameof(DeviceViewModel.RightArtworkPath) or nameof(DeviceViewModel.CaseArtworkPath))) return;
        // Metadata arrives asynchronously on a first connection. Refresh the compact shot in place;
        // video layouts with their own timing are selected on the next connection.
        var path = IsDark ? ViewModel.PopupMediaDarkPath : ViewModel.PopupMediaPath;
        bool video = IsDark ? ViewModel.PopupMediaDarkIsVideo : ViewModel.PopupMediaIsVideo;
        if (_headphones)
        {
            HeadphonePicture.FallbackPath = ViewModel.DeviceImagePath;
            HeadphonePicture.SourcePath = video ? ViewModel.DeviceImagePath : path ?? ViewModel.DeviceImagePath;
            return;
        }
        ConfigureCompactArtwork(video && !(path?.Contains(".reconnect.", StringComparison.OrdinalIgnoreCase) ?? false) && !(path?.EndsWith(".alpha-right.mp4", StringComparison.OrdinalIgnoreCase) ?? false) ? ViewModel.DeviceImagePath : path);
    }

    private void AnimateCompactArtwork(bool components)
    {
        if (!_animate) return;
        void Reveal(FrameworkElement element, float rise, int delay)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var c = visual.Compositor;
            visual.StartAnimation("Translation", Vector(c, new Vector3(0, rise, 0), Vector3.Zero, 650, delay,
                c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1))));
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1);
            fade.Duration = TimeSpan.FromMilliseconds(450);
            fade.DelayTime = TimeSpan.FromMilliseconds(delay);
            visual.StartAnimation("Opacity", fade);
        }
        if (components) { Reveal(CaseArt, 10, 0); Reveal(LeftArt, 26, 90); Reveal(RightArt, 26, 140); }
        else Reveal(Picture, 12, 0);
    }

    // HeyMelody's colours where a pack sets none (night variants in dark mode): the dialog title
    // (melody_app_discovery_title_text_color), the level text (coui_color_secondary_neutral) and the
    // battery outline (melody_ui_iot_battery_out_stroke_tint at its 0.51 stroke alpha).
    private static readonly Color TitleLight = Color.FromArgb(0xD9, 0, 0, 0);
    private static readonly Color TitleDark = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
    private static readonly Color LevelTextLight = Color.FromArgb(0x8A, 0, 0, 0);
    private static readonly Color LevelTextDark = Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF);
    private static readonly Color OutlineLight = Color.FromArgb(0xD9, 0, 0, 0);
    private static readonly Color OutlineDark = Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF);
    private const double OutlineAlpha = 0.51;

    private static Color? ToColor(string? text) =>
        PopupLayout.ParseArgb(text) is { } argb ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb) : null;

    /// <summary>Background, title and the battery levels for the levels known now.</summary>
    private void ApplyOfficialLayout()
    {
        bool dark = IsDark;
        // The pack's background colour fills the dialog behind the video (and instead of it while hidden).
        if (ToColor(_layout.Background.Pick(dark)) is { } background) OfficialLayout.Background = new SolidColorBrush(background);

        TitleText.Visibility = _layout.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Foreground = new SolidColorBrush(ToColor(_layout.TitleColor.Pick(dark)) ?? (dark ? TitleDark : TitleLight));
        TitleText.Text = _layout.TitleFormat is { } format
            ? format.Replace("%1$s", ViewModel.DeviceName, StringComparison.Ordinal).Replace("%s", ViewModel.DeviceName, StringComparison.Ordinal)
            : Loc.Format("Popup_ConnectedTitle", ViewModel.DeviceName);
        UpdateBatteries();
    }

    private void OnBatteryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_finished || _timeline is null) return;
        if (e.PropertyName is nameof(BatteryItemViewModel.Percent) or nameof(BatteryItemViewModel.HasValue) or nameof(BatteryItemViewModel.IsCharging)) UpdateBatteries();
    }

    private static int Level(BatteryItemViewModel item) => item.HasValue ? (int)Math.Round(item.Percent) : 0;

    /// <summary>Rebuilds the groups (or the column) for the current levels, as HeyMelody does on each battery update.</summary>
    private void UpdateBatteries()
    {
        bool dark = IsDark;
        var arrangement = _layout.Arrange(Level(ViewModel.Left), Level(ViewModel.Right), Level(ViewModel.Case), hasVideo: _videoPath is not null);
        bool imagesChanged = _arrangement is null || !SameImages(_arrangement, arrangement);
        _arrangement = arrangement;
        Video.Visibility = arrangement.VideoVisible ? Visibility.Visible : Visibility.Collapsed;

        BuildLevels(arrangement, darkText: !dark);
        // Packs that set no colours get the theme's default, which HeyMelody also uses. On a video
        // that is light where the levels sit (Enco X4's night video) that would be unreadable:
        // follow the video instead of the theme.
        if (arrangement.VideoVisible && _layout.BatteryText.Pick(dark) is null && LevelsLuminance() is { } luminance)
        {
            bool darkText = luminance > 0.55;
            if (darkText == dark) BuildLevels(arrangement, darkText);
        }
        if (imagesChanged) BuildImages(arrangement);
    }

    /// <summary>Luminance of the video behind the battery levels, or null before the first frame.</summary>
    private double? LevelsLuminance()
    {
        if (_video is not { VideoWidth: > 0 } video || LevelLayer.Children.Count == 0) return null;
        double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
        foreach (var child in LevelLayer.Children.OfType<FrameworkElement>())
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = Canvas.GetLeft(child), y = Canvas.GetTop(child);
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + child.DesiredSize.Width);
            bottom = Math.Max(bottom, y + child.DesiredSize.Height);
        }
        // The video fills the dialog's width and starts at its top (same aspect when not compact).
        double videoHeight = PopupLayout.Width * video.VideoHeight / video.VideoWidth;
        return video.Luminance(left / PopupLayout.Width, top / videoHeight, right / PopupLayout.Width, bottom / videoHeight);
    }

    private void BuildLevels(PopupBatteryArrangement arrangement, bool darkText)
    {
        bool dark = !darkText;
        var text = new SolidColorBrush(ToColor(_layout.BatteryText.Pick(IsDark)) ?? (dark ? LevelTextDark : LevelTextLight));
        var outlineColor = ToColor(_layout.BatteryOutline.Pick(IsDark)) ?? (dark ? OutlineDark : OutlineLight);
        var outline = new SolidColorBrush(Color.FromArgb((byte)Math.Round(outlineColor.A * OutlineAlpha), outlineColor.R, outlineColor.G, outlineColor.B));
        var fill = ToColor(_layout.BatteryFill.Pick(IsDark)) ?? PopupBatteryLevel.Green;

        LevelLayer.Children.Clear();
        foreach (var group in arrangement.Groups)
        {
            var rect = PopupLayout.GroupRect(group.Group);
            foreach (var (slot, battery) in group.Slots)
            {
                var place = PopupLayout.SlotRect(slot);
                var level = NewLevel(battery, vertical: false, text, outline, fill);
                Canvas.SetLeft(level, rect.X + place.X);
                Canvas.SetTop(level, rect.Y + place.Y);
                LevelLayer.Children.Add(level);
            }
        }
        if (arrangement.Column.Count > 0)
        {
            var column = new StackPanel();
            foreach (var battery in arrangement.Column) column.Children.Add(NewLevel(battery, vertical: true, text, outline, fill));
            PlaceColumn(column);
            LevelLayer.Children.Add(column);
        }
    }

    private PopupBatteryLevel NewLevel(PopupBattery battery, bool vertical, Brush text, Brush outline, Color fill)
    {
        var item = battery switch
        {
            PopupBattery.Left => ViewModel.Left,
            PopupBattery.Right => ViewModel.Right,
            _ => ViewModel.Case,
        };
        var level = new PopupBatteryLevel();
        level.Show(item.Component, Level(item), item.IsCharging, vertical, text, outline, fill);
        return level;
    }

    /// <summary>The column at its gravity edges and margins (dp of the 320 × 347 dialog).</summary>
    private void PlaceColumn(StackPanel column)
    {
        column.Measure(new Size(PopupLayout.Width, PopupLayout.Height));
        var size = column.DesiredSize;
        var (left, top, right, bottom) = _layout.ColumnMargin;
        double x = _layout.ColumnHorizontal switch
        {
            PopupEdge.End => PopupLayout.Width - right - size.Width,
            PopupEdge.Center => (PopupLayout.Width - size.Width) / 2 + left - right,
            _ => left,
        };
        double y = _layout.ColumnVertical switch
        {
            PopupEdge.End => PopupLayout.Height - bottom - size.Height,
            PopupEdge.Center => (PopupLayout.Height - size.Height) / 2 + top - bottom,
            _ => top,
        };
        Canvas.SetLeft(column, Math.Max(0, x));
        Canvas.SetTop(column, Math.Max(0, y));
    }

    private static bool SameImages(PopupBatteryArrangement a, PopupBatteryArrangement b) =>
        a.ImagesVisible == b.ImagesVisible && a.ForegroundVisible == b.ForegroundVisible
        && a.Groups.Select(g => (g.Group, g.Image)).SequenceEqual(b.Groups.Select(g => (g.Group, g.Image)));

    /// <summary>
    /// The stand-in pictures (earbuds / case) above the levels when the video is not shown, or the
    /// pack's foreground animation. Both enter together after <c>resShowDelay</c>.
    /// </summary>
    private void BuildImages(PopupBatteryArrangement arrangement)
    {
        ImageLayer.Children.Clear();
        _foreground = null;
        if (_packRoot is null) return;
        if (arrangement.ForegroundVisible) AddForeground();
        if (!arrangement.ImagesVisible) return;
        foreach (var group in arrangement.Groups)
        {
            string? relative = group.Image switch
            {
                PopupImage.Box => _layout.BoxImage,
                PopupImage.None => null,
                _ => _layout.BudsImage,
            };
            if (relative is null) continue;
            string file = Path.GetFullPath(Path.Combine(_packRoot, relative));
            if (!file.StartsWith(_packRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) continue;
            var rect = PopupLayout.GroupRect(group.Group);
            var host = new Canvas { Width = PopupLayout.GroupWidth, Height = PopupLayout.GroupImageHeight };
            Canvas.SetLeft(host, rect.X);
            Canvas.SetTop(host, rect.Y);
            var image = new Image();
            var bitmap = new BitmapImage(new Uri(file));
            var kind = group.Image;
            bitmap.ImageOpened += (_, _) => FitPicture(image, bitmap.PixelWidth, bitmap.PixelHeight, kind);
            image.Source = bitmap;
            host.Children.Add(image);
            ImageLayer.Children.Add(host);
        }
    }

    /// <summary>
    /// The foreground animation (an animated WebP, looping at its own timing) in its band above the
    /// levels. It stays invisible until the file has loaded as an animation, so
    /// neither the generic illustration nor an empty band ever shows.
    /// </summary>
    private void AddForeground()
    {
        if (_layout.ForegroundAnimation is not { } relative) return;
        string file = Path.GetFullPath(Path.Combine(_packRoot!, relative));
        if (!file.StartsWith(_packRoot!, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return;
        var rect = PopupLayout.ForegroundRect;
        var foreground = new DeviceModelView
        {
            Width = rect.Width,
            Height = rect.Height,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
        };
        foreground.ModelAvailabilityChanged += (_, available) => foreground.Opacity = available ? 1 : 0;
        Canvas.SetLeft(foreground, rect.X);
        Canvas.SetTop(foreground, rect.Y);
        foreground.ModelPath = file;
        ImageLayer.Children.Add(foreground);
        _foreground = foreground;
    }

    /// <summary>
    /// Fits a picture into the 130 dp square (FIT_CENTER). A half earbuds picture is HeyMelody's
    /// HalfBitmapTransformation: one half of the picture on a canvas 0.6308 of its width.
    /// </summary>
    private static void FitPicture(Image image, int width, int height, PopupImage kind)
    {
        if (width <= 0 || height <= 0) return;
        bool half = kind is PopupImage.BudsLeftHalf or PopupImage.BudsRightHalf;
        double canvasWidth = half ? width * 0.63076925 : width;
        double scale = Math.Min(PopupLayout.GroupWidth / canvasWidth, PopupLayout.GroupImageHeight / height);
        double x = (PopupLayout.GroupWidth - canvasWidth * scale) / 2;
        double y = (PopupLayout.GroupImageHeight - height * scale) / 2;
        image.Stretch = Stretch.Fill;
        image.Width = width * scale;
        image.Height = height * scale;
        Canvas.SetTop(image, y);
        if (kind == PopupImage.BudsRightHalf)
        {
            Canvas.SetLeft(image, x + (canvasWidth - width) * scale);
            image.Clip = new RectangleGeometry { Rect = new Rect(width / 2.0 * scale, 0, width / 2.0 * scale, height * scale) };
        }
        else
        {
            Canvas.SetLeft(image, x);
            if (half) image.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width / 2.0 * scale, height * scale) };
        }
    }

    // ---- official timeline -------------------------------------------------------------------------

    /// <summary>
    /// Plays <c>[startTime, endTime]</c>, then holds the last frame or loops from <c>loopTime</c>
    /// (HeyMelody onVideoStart / onVideoEnd), decoded at the size it is shown.
    /// </summary>
    private void StartVideo(int pixelHeight)
    {
        if (_videoPath is not { } path || _timeline is not { } timeline) return;
        _video = new SilentVideoPresenter(Video);
        if (_layout.Background.Pick(IsDark) is null)
        {
            // No pack colour: the video's own bottom edge fills the window where the video is not shown.
            _video.FirstFrameShown += (_, _) =>
            {
                if (!_finished && _video?.BottomEdgeColor is { } edgeColor) OfficialLayout.Background = new SolidColorBrush(edgeColor);
            };
        }
        if (_layout.VideoFadesIn && _animate)
        {
            // animation bit 1: the video fades in over 600 ms when it first plays.
            Video.Opacity = 0;
            _video.FirstFrameShown += (_, _) =>
            {
                if (_finished) return;
                var visual = ElementCompositionPreview.GetElementVisual(Video);
                var c = visual.Compositor;
                Video.Opacity = 1;
                visual.StartAnimation("Opacity", Scalar(c, 0, 1, 600, 0, c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1))));
            };
        }
        _video.Play(path, new VideoSegment(timeline.VideoStart, timeline.VideoEnd, timeline.LoopFrom), pixelHeight);
    }

    /// <summary>Title, battery levels and pictures appear on HeyMelody's timeline.</summary>
    private void StartReveals()
    {
        if (_huawei) { After(TimeSpan.FromMilliseconds(_huaweiTextDelay), () => Reveal(HuaweiLevels, 300)); return; }
        if (_timeline is not { } timeline) return;
        // Title: 200 ms fade (DiscoveryAnimUtils.setVisibility); levels: 300 ms fade (alpha_show_anim).
        After(timeline.TitleDelay, () => Reveal(TitleText, 200));
        After(timeline.InfoDelay, () =>
        {
            UpdateBatteries(); // colours against the frame that is showing now
            Reveal(LevelLayer, 300);
        });
        ImageLayer.Opacity = 0;
        After(timeline.ImageDelay, () => RevealImages(timeline.Entrance));
    }

    private void After(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_finished) action();
        };
        _stepTimers.Add(timer);
        timer.Start();
    }

    private void Reveal(UIElement element, int milliseconds)
    {
        element.Opacity = 1;
        if (!_animate) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StartAnimation("Opacity", Scalar(visual.Compositor, 0, 1, milliseconds, 0, visual.Compositor.CreateLinearEasingFunction()));
    }

    /// <summary><c>resInAnim</c>: slide in from the right edge over 1 s, zoom from 80 % with a fade, or fade.</summary>
    private void RevealImages(PopupEntrance entrance)
    {
        ImageLayer.Opacity = 1;
        if (!_animate) return;
        var visual = ElementCompositionPreview.GetElementVisual(ImageLayer);
        var c = visual.Compositor;
        switch (entrance)
        {
            case PopupEntrance.RightSlideIn:
                ElementCompositionPreview.SetIsTranslationEnabled(ImageLayer, true);
                // Android's default view-animation interpolator: accelerate-decelerate.
                visual.StartAnimation("Translation", Vector(c, new Vector3((float)PopupLayout.Width, 0, 0), Vector3.Zero, 1000, 0,
                    c.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0), new Vector2(0.63f, 1))));
                break;
            case PopupEntrance.ZoomIn:
                visual.CenterPoint = new Vector3((float)PopupLayout.Width / 2, (float)PopupLayout.Height / 2, 0);
                visual.StartAnimation("Scale", Vector(c, new Vector3(0.8f, 0.8f, 1), Vector3.One, 300, 0, c.CreateLinearEasingFunction()));
                visual.StartAnimation("Opacity", Scalar(c, 0, 1, 300, 0, c.CreateLinearEasingFunction()));
                break;
            default:
                visual.StartAnimation("Opacity", Scalar(c, 0, 1, 300, 0, c.CreateLinearEasingFunction()));
                break;
        }
    }

    // ---- click-through guard -----------------------------------------------------------------------

    private void StartGuard()
    {
        _guardTimer = DispatcherQueue.CreateTimer();
        _guardTimer.Interval = GuardInterval;
        _guardTimer.Tick += (_, _) => Guard();
        _guardTimer.Start();
    }

    /// <summary>Re-checked while visible: a game started now must never be blocked by the pop-up.</summary>
    private void Guard()
    {
        if (_huawei && HuaweiPicture.IsVideoHolding && !_closing) { Dismiss(); return; }
        if (_closing || SuspendGuardForDiagnostics) return;
        // Seen dropping out of the topmost band while another app is in the foreground: put it back.
        if (!WindowInterop.IsTopmost(this)) _ = WindowInterop.EnsureTopmost(this);
        switch (FullscreenDetector.GetState())
        {
            case InterruptionState.ExclusiveFullscreen:
            case InterruptionState.Away:
                DismissNow();
                return;
            case InterruptionState.Fullscreen when !_passThrough:
                EnterClickThrough();
                return;
        }
        if (_passThrough && WindowInterop.ReceivesMouseAt(this, Center)) DismissNow();
    }

    /// <summary>
    /// Lets every mouse event through to the window underneath and verifies it: if the system would
    /// still route the mouse to the pop-up, it closes at once instead of blocking anything.
    /// </summary>
    private void EnterClickThrough()
    {
        _passThrough = true;
        WindowInterop.SetClickThrough(this, true);
        OfficialClose.Visibility = Visibility.Collapsed;
        CardClose.Visibility = Visibility.Collapsed;
        HeadphoneClose.Visibility = Visibility.Collapsed;
        if (_hovered)
        {
            // Hover paused the countdown; no pointer events arrive any more, so resume it.
            _hovered = false;
            StartCloseTimer();
        }
        if (WindowInterop.ReceivesMouseAt(this, Center)) DismissNow();
    }

    // ---- entrance / dismissal ----------------------------------------------------------------------

    // ---- window-level entrance / exit ---------------------------------------------------------------

    /// <summary>The bounds moved <paramref name="amount"/> (0–1) of the slide distance towards the screen edge.</summary>
    private PixelRect Slid(double amount)
    {
        int d = (int)Math.Round(_slideDistance * amount);
        return _bounds with { Y = _bounds.Y + (_slideFromTop ? -d : d) };
    }

    /// <summary>
    /// Fades the whole window (layered alpha) while it slides in from, or back towards, its screen
    /// edge, like HeyMelody's bottom sheet. Nothing behind the content is ever shown.
    /// <para>
    /// Driven once per displayed frame (<see cref="CompositionTarget.Rendering"/>) and moving the
    /// window without resizing it, so the motion is as smooth as the display. The entrance decelerates
    /// into place; the exit eases in and out, so neither starts nor stops abruptly. An exit started
    /// during the entrance continues from where the entrance was.
    /// </para>
    /// </summary>
    private void AnimateWindow(bool entering, Action? completed)
    {
        StopFrames();
        double from = _shown;
        double to = entering ? 1 : 0;
        double duration = (entering ? EnterMilliseconds : ExitMilliseconds) * Math.Max(0.35, Math.Abs(to - from));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _frame = (_, _) =>
        {
            if (_finished) { StopFrames(); return; }
            double p = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration, 0, 1);
            double eased = entering ? EaseOutQuint(p) : EaseInOutCubic(p);
            ApplyShown(from + (to - from) * eased);
            if (p < 1) return;
            StopFrames();
            completed?.Invoke();
        };
        CompositionTarget.Rendering += _frame;
    }

    /// <summary>How far the pop-up is shown (0 hidden at its edge, 1 in place).</summary>
    private double _shown;

    private void ApplyShown(double shown)
    {
        _shown = shown;
        WindowInterop.SetWindowAlpha(this, (byte)Math.Round(255 * Math.Clamp(shown, 0, 1)));
        var at = Slid(1 - shown);
        WindowInterop.MoveTo(this, at.X, at.Y);
    }

    private void StopFrames()
    {
        if (_frame is null) return;
        CompositionTarget.Rendering -= _frame;
        _frame = null;
    }

    private static double EaseOutQuint(double t) => 1 - Math.Pow(1 - t, 5);

    private static double EaseInOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        StopTimers();
        if (!_animate)
        {
            Finish();
            return;
        }
        AnimateWindow(entering: false, Finish);
    }

    /// <summary>Closes without any animation (fullscreen started, or click-through could not be guaranteed).</summary>
    public void DismissNow()
    {
        if (_finished) return;
        _closing = true;
        StopTimers();
        StopFrames();
        Finish();
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        // No decoding while hidden; the last frame stays until the window is gone.
        _video?.Pause();
        // Invisible and hidden before the window is torn down: no black frame.
        WindowInterop.SetWindowAlpha(this, 0);
        WindowInterop.GetAppWindow(this).Hide();
        Close();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void StartCloseTimer()
    {
        _closeTimer ??= DispatcherQueue.CreateTimer();
        _closeTimer.Interval = _huawei ? TimeSpan.FromSeconds(30) : _timeline?.CloseDelay ?? _fallbackDuration;
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick -= OnCloseTick;
        _closeTimer.Tick += OnCloseTick;
        _closeTimer.Start();
    }

    private void OnCloseTick(DispatcherQueueTimer sender, object args) => Dismiss();

    private void StopTimers()
    {
        _closeTimer?.Stop();
        _guardTimer?.Stop();
        foreach (var timer in _stepTimers) timer.Stop();
        _stepTimers.Clear();
    }

    private void Release()
    {
        ViewModel.PropertyChanged -= OnArtworkChanged;
        StopTimers();
        StopFrames();
        if (_closeTimer is not null) _closeTimer.Tick -= OnCloseTick;
        _closeTimer = null;
        foreach (var item in new[] { ViewModel.Left, ViewModel.Right, ViewModel.Case }) item.PropertyChanged -= OnBatteryChanged;
        // Ends the decode thread, which releases the reader and its Media Foundation objects.
        _video?.Stop();
        _video = null;
        Picture.Stop();
        HeadphonePicture.Stop();
        HuaweiPicture.Stop();
    }

    // ---- interaction (only when not click-through) -------------------------------------------------

    // Hovering pauses the countdown; leaving restarts it.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_passThrough || _closing) return;
        _hovered = true;
        Picture.Pause();
        _closeTimer?.Stop();
        if (_timeline is not null) OfficialClose.Visibility = Visibility.Visible;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_passThrough || _closing) return;
        _hovered = false;
        Picture.Resume();
        OfficialClose.Visibility = Visibility.Collapsed;
        if (!_closing) StartCloseTimer();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_passThrough || _closing) return;
        // A tap on the close button (or anything inside it, such as its icon) only closes.
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        App.Instance.Windows.ShowMain("Home");
        Dismiss();
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase) return true;
        }
        return false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();

    private static ScalarKeyFrameAnimation Scalar(Compositor c, float from, float to, int ms, int delayMs, CompositionEasingFunction ease)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, ease);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return a;
    }

    private static Vector3KeyFrameAnimation Vector(Compositor c, Vector3 from, Vector3 to, int ms, int delayMs, CompositionEasingFunction ease)
    {
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, from);
        a.InsertKeyFrame(1, to, ease);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return a;
    }
}
