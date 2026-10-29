// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text.Json;

namespace EncoDeck.Platform.Resources;

/// <summary>How the battery levels are laid out (HeyMelody <c>batteryGroupStyle</c>).</summary>
public enum PopupBatteryStyle
{
    /// <summary>Groups under the earbuds and the case (DiscoveryBatteryGroupHorizontalImpl).</summary>
    Horizontal,
    /// <summary>One column of L / R / case rows (DiscoveryBatteryGroupVerticalImpl).</summary>
    Vertical,
}

/// <summary>A battery group of the horizontal style; each is 130 × 162 dp, its bottom on the 55 % line.</summary>
public enum PopupGroup
{
    /// <summary>Centred on the 28 % line, under the earbuds of the video.</summary>
    Buds,
    /// <summary>Centred on the 70 % line, under the case of the video.</summary>
    Box,
    /// <summary>Centred in the pop-up (the "holder" used when the video is not shown).</summary>
    Center,
}

/// <summary>One of the three 40 × 32 dp places along the bottom of a group.</summary>
public enum PopupSlot
{
    Left,
    Center,
    Right,
}

public enum PopupBattery
{
    Left,
    Right,
    Case,
}

/// <summary>The picture above a group's levels (only shown when the video is not).</summary>
public enum PopupImage
{
    None,
    Buds,
    /// <summary>The left half of the earbuds picture (only the left earbud reports a level).</summary>
    BudsLeftHalf,
    BudsRightHalf,
    Box,
}

/// <summary>Where a vertical battery column sits: edges and margins in dp of the 320 × 347 pop-up.</summary>
public enum PopupEdge
{
    Start,
    Center,
    End,
}

public sealed record PopupGroupContent(PopupGroup Group, PopupImage Image, IReadOnlyList<(PopupSlot Slot, PopupBattery Battery)> Slots);

/// <summary>What the pop-up shows for the battery levels known right now.</summary>
/// <param name="ForegroundVisible">The pack's foreground animation plays over the video (HeyMelody's flag 8).</param>
public sealed record PopupBatteryArrangement(
    bool VideoVisible,
    bool ImagesVisible,
    IReadOnlyList<PopupGroupContent> Groups,
    IReadOnlyList<PopupBattery> Column,
    bool ForegroundVisible = false);

/// <summary>A colour with an optional night variant (HeyMelody picks the night one in dark mode when set).</summary>
public sealed record PopupColor(string? Day, string? Night = null)
{
    public static PopupColor None { get; } = new(null, null);

    public string? Pick(bool dark) => dark && Night is not null ? Night : Day;
}

/// <summary>A rectangle in dp of the 320 × 347 pop-up.</summary>
public readonly record struct PopupRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// Layout of HeyMelody's connection pop-up ("discovery dialog") for one pack, from its
/// <c>config.json</c>. Positions are in dp of the dialog HeyMelody draws: 320 × 347 dp
/// (<c>melody_app_discovery_dialog_width/height</c>), the aspect of its 960 × 1040 videos, which
/// EncoDeck scales to its pop-up size.
/// <para>
/// Reproduces (HeyMelody 15.8.1): <c>DiscoveryDialogActivity</c> (video, background colour,
/// <c>animation</c> flags), <c>DiscoveryViewStateConnectedImpl</c> (state choice, delays, title),
/// <c>DiscoveryBatteryGroupHorizontalImpl</c> / <c>…VerticalImpl</c> (where the levels go) and the
/// <c>melody_app_include_discovery_earphone</c> layout (guidelines at 28 % / 70 % / 55 % / 58 %).
/// </para>
/// </summary>
public sealed record PopupLayout
{
    public const double Width = 320;
    public const double Height = 347;
    public const double GroupWidth = 130;
    public const double GroupImageHeight = 130;
    public const double SlotWidth = 40;
    public const double SlotHeight = 32;
    public const double GroupHeight = GroupImageHeight + SlotHeight;
    /// <summary>The 0.55 guideline the groups stand on.</summary>
    public const double GroupBottom = Height * 0.55;
    /// <summary>Title band: from the 0.58 guideline to 68 dp above the bottom, 24 dp from the sides.</summary>
    public static readonly PopupRect TitleBand = new(24, Height * 0.58, Width - 48, Height - 68 - Height * 0.58);
    /// <summary>
    /// The foreground animation (<c>connected_foreground_anim</c>): full width, 100 dp high, 34 dp from
    /// the top, the picture fitted and centred in it. Packs draw the product there (e.g. Enco Free4
    /// Dynaudio: earbuds and case over a video of the brand's logo alone).
    /// </summary>
    public static readonly PopupRect ForegroundRect = new(0, 34, Width, 100);

    /// <summary>Compact layout: gap between the battery row and the title, and below the title (dp).</summary>
    public const double CompactTitleGap = 19;
    public const double CompactBottomPadding = 24;
    /// <summary>Compact heights are kept within 240–368 of the pop-up's 368 DIP (as dp of the dialog).</summary>
    public const double CompactMinHeight = Height * 240 / 368;

    /// <summary>
    /// Product-shot packs (Enco X3, Free4, …: a video segment with the battery groups under the
    /// earbuds and the case, then the title) leave the lower part of the dialog empty, where
    /// HeyMelody has its buttons. EncoDeck has none, so the pop-up ends below the title instead: the
    /// video is aligned to the top and cut at the bottom, the pack's background colour (or the
    /// video's own bottom edge) filling the window. Full-bleed artwork (the vertical column style,
    /// e.g. Enco X4) keeps the whole dialog.
    /// </summary>
    public bool IsCompact => BatteryStyle == PopupBatteryStyle.Horizontal && HasVideoSegment;

    /// <summary>
    /// The dialog height to show (dp): the full 347 dp, or for a compact layout the battery row,
    /// the title (<paramref name="titleHeight"/>, measured; 0 when hidden) and the paddings.
    /// </summary>
    public double ContentHeight(double titleHeight)
    {
        if (!IsCompact) return Height;
        double height = GroupBottom + (titleHeight > 0 ? CompactTitleGap + titleHeight : 0) + CompactBottomPadding;
        return Math.Clamp(height, CompactMinHeight, Height);
    }

    /// <summary>A pack that sets nothing: horizontal groups, theme colours, title shown.</summary>
    public static PopupLayout Default { get; } = new();

    public PopupBatteryStyle BatteryStyle { get; init; } = PopupBatteryStyle.Horizontal;

    /// <summary>Vertical column placement; HeyMelody's default is 20 dp from the top, 270 dp from the left.</summary>
    public PopupEdge ColumnHorizontal { get; init; } = PopupEdge.Start;

    public PopupEdge ColumnVertical { get; init; } = PopupEdge.Start;

    /// <summary>Column margins in dp (left, top, right, bottom).</summary>
    public (double Left, double Top, double Right, double Bottom) ColumnMargin { get; init; } = (270, 20, 0, 0);

    /// <summary><c>videoRes.backgroundColor</c> / <c>nightBackgroundColor</c>: fills the pop-up behind the video.</summary>
    public PopupColor Background { get; init; } = PopupColor.None;

    /// <summary><c>animation</c> bit 1: the video fades in (600 ms) when it first plays.</summary>
    public bool VideoFadesIn { get; init; }

    public bool ShowTitle { get; init; } = true;

    /// <summary><c>titleRes.text</c>: a format with the device name as <c>%s</c>, or null for "Connected {name}".</summary>
    public string? TitleFormat { get; init; }

    public PopupColor TitleColor { get; init; } = PopupColor.None;

    /// <summary><c>batteriesRes.textColor</c>.</summary>
    public PopupColor BatteryText { get; init; } = PopupColor.None;

    /// <summary><c>batteriesRes.backgroundColor</c>: tints the battery outline (and the column's device icons).</summary>
    public PopupColor BatteryOutline { get; init; } = PopupColor.None;

    /// <summary><c>batteriesRes.foregroundColor</c>: the level fill (red at 20 % or less regardless).</summary>
    public PopupColor BatteryFill { get; init; } = PopupColor.None;

    /// <summary>Pack-relative paths of the stand-in pictures (<c>budsImageRes</c> / <c>boxImageRes</c>).</summary>
    public string? BudsImage { get; init; }

    public string? BoxImage { get; init; }

    public bool ShowImageRes { get; init; }

    /// <summary>Pack-relative path of the state's <c>fgAnimRes</c> (an animated WebP), or null.</summary>
    public string? ForegroundAnimation { get; init; }

    public bool HasForegroundAnimation => ForegroundAnimation is not null;

    /// <summary>The state plays a video segment (<c>startTime</c> set); otherwise HeyMelody shows pictures only.</summary>
    public bool HasVideoSegment { get; init; }

    /// <summary>The layout of the pack <paramref name="mediaPath"/> belongs to (see <see cref="PopupTimeline.FindConfigFor"/>), or null.</summary>
    public static PopupLayout? TryReadFor(string mediaPath)
    {
        try
        {
            return PopupTimeline.FindConfigFor(mediaPath) is { } config ? Parse(File.ReadAllText(config)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Same file as <see cref="PopupTimeline"/>: the pop-up state HeyMelody uses for a known device.</summary>
    public static PopupLayout? Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (PopupTimeline.SelectState(root) is not { } state) return null;
            var video = Object(root, "videoRes");
            var batteries = Object(state, "batteriesRes");
            var title = Object(state, "titleRes");

            int style = Int(state, "batteryGroupStyle") ?? 0;
            var layout = new PopupLayout
            {
                Background = Colour(video, "backgroundColor", "backgroundColorNight", "nightBackgroundColor"),
                VideoFadesIn = ((Int(state, "animation") ?? 0) & 1) != 0,
                ShowTitle = !(state.TryGetProperty("showTitleRes", out var showTitle) && showTitle.ValueKind == JsonValueKind.False),
                TitleFormat = String(title, "text"),
                TitleColor = Colour(title, "textColor", "textColorNight"),
                BatteryText = Colour(batteries, "textColor", "textColorNight"),
                BatteryOutline = Colour(batteries, "backgroundColor", "backgroundColorNight", "nightBackgroundColor"),
                BatteryFill = Colour(batteries, "foregroundColor", "foregroundColorNight"),
                BudsImage = String(Object(state, "budsImageRes"), "path"),
                BoxImage = String(Object(state, "boxImageRes"), "path"),
                ShowImageRes = state.TryGetProperty("showImageRes", out var showImage) && showImage.ValueKind == JsonValueKind.True,
                ForegroundAnimation = String(Object(state, "fgAnimRes"), "path"),
                HasVideoSegment = state.TryGetProperty("startTime", out var start) && start.ValueKind == JsonValueKind.Number,
            };
            // Style 1 is HeyMelody's vertical column. Newer packs (style 3, e.g. Enco X4) place the
            // column with batteriesRes gravity and margins, which this HeyMelody version does not
            // read; they are honoured here, the margins taken as px at 3× (the packs' 960 px video).
            int? gravity = Int(batteries, "gravity");
            if (style == 1 || (style == 3 && gravity is not null)) layout = layout with { BatteryStyle = PopupBatteryStyle.Vertical };
            if (layout.BatteryStyle == PopupBatteryStyle.Vertical && gravity is { } g)
            {
                double Px(string name) => (Number(batteries, name) ?? 0) / 3;
                layout = layout with
                {
                    ColumnHorizontal = HorizontalEdge(g),
                    ColumnVertical = VerticalEdge(g),
                    ColumnMargin = (Px("leftMargin"), Px("topMargin"), Px("rightMargin"), Px("bottomMargin")),
                };
            }
            return layout;
        }
    }

    /// <summary>
    /// Which groups, slots and pictures show for these levels (0 = unknown), as HeyMelody's battery
    /// group decides on every battery update.
    /// </summary>
    /// <param name="hasVideo">The pop-up video file exists.</param>
    public PopupBatteryArrangement Arrange(int left, int right, int box, bool hasVideo)
    {
        bool all = left > 0 && right > 0 && box > 0;
        // A pack with a background colour hides its video until all three levels are known and
        // shows the stand-in pictures instead (DiscoveryViewStateConnectedImpl, "canHideVideo").
        bool videoVisible = hasVideo && (all || Background.Day is null);

        if (BatteryStyle == PopupBatteryStyle.Vertical)
        {
            var column = new List<PopupBattery>(3);
            if (left > 0) column.Add(PopupBattery.Left);
            if (right > 0) column.Add(PopupBattery.Right);
            if (box > 0) column.Add(PopupBattery.Case);
            return new PopupBatteryArrangement(videoVisible, false, [], column);
        }

        static PopupGroupContent Group(PopupGroup group, PopupImage image, params (PopupSlot, PopupBattery)[] slots) => new(group, image, slots);
        var buds = BudsImage is null ? PopupImage.None : PopupImage.Buds;
        var boxImage = BoxImage is null ? PopupImage.None : PopupImage.Box;

        if (all || (hasVideo && HasVideoSegment && Background.Day is null))
        {
            if (HasForegroundAnimation || (hasVideo && HasVideoSegment && !ShowImageRes))
            {
                // The usual case: levels under the earbuds and under the case of the video, or of
                // the foreground animation when the pack draws them there.
                return new PopupBatteryArrangement(videoVisible, false,
                    [Group(PopupGroup.Buds, PopupImage.None, (PopupSlot.Left, PopupBattery.Left), (PopupSlot.Right, PopupBattery.Right)),
                     Group(PopupGroup.Box, PopupImage.None, (PopupSlot.Center, PopupBattery.Case))], [], HasForegroundAnimation);
            }
            if (BoxImage is null)
            {
                return new PopupBatteryArrangement(videoVisible, true,
                    [Group(PopupGroup.Center, buds, (PopupSlot.Left, PopupBattery.Left), (PopupSlot.Right, PopupBattery.Right))], []);
            }
            return new PopupBatteryArrangement(videoVisible, true,
                [Group(PopupGroup.Buds, buds, (PopupSlot.Left, PopupBattery.Left), (PopupSlot.Right, PopupBattery.Right)),
                 Group(PopupGroup.Box, boxImage, (PopupSlot.Center, PopupBattery.Case))], []);
        }

        var one = left > 0 ? PopupBattery.Left : PopupBattery.Right;
        var half = BudsImage is null ? PopupImage.None : left > 0 ? PopupImage.BudsLeftHalf : PopupImage.BudsRightHalf;
        if (box > 0 && (left > 0 || right > 0))
        {
            if (BoxImage is null)
            {
                return new PopupBatteryArrangement(videoVisible, true, [Group(PopupGroup.Center, half, (PopupSlot.Center, one))], []);
            }
            return new PopupBatteryArrangement(videoVisible, true,
                [Group(PopupGroup.Buds, half, (PopupSlot.Center, one)),
                 Group(PopupGroup.Box, boxImage, (PopupSlot.Center, PopupBattery.Case))], []);
        }
        if (box > 0) return new PopupBatteryArrangement(videoVisible, true, [Group(PopupGroup.Center, boxImage, (PopupSlot.Center, PopupBattery.Case))], []);
        if (left > 0 && right > 0)
        {
            return new PopupBatteryArrangement(videoVisible, true,
                [Group(PopupGroup.Center, buds, (PopupSlot.Left, PopupBattery.Left), (PopupSlot.Right, PopupBattery.Right))], []);
        }
        if (left > 0 || right > 0) return new PopupBatteryArrangement(videoVisible, true, [Group(PopupGroup.Center, half, (PopupSlot.Center, one))], []);
        return new PopupBatteryArrangement(videoVisible, true, [Group(PopupGroup.Center, buds)], []);
    }

    /// <summary>A group's rectangle: 130 × 162 dp, centred on its guideline, bottom on the 55 % line.</summary>
    public static PopupRect GroupRect(PopupGroup group)
    {
        double centre = group switch
        {
            PopupGroup.Buds => Width * 0.28,
            PopupGroup.Box => Width * 0.70,
            _ => Width / 2,
        };
        return new PopupRect(centre - GroupWidth / 2, GroupBottom - GroupHeight, GroupWidth, GroupHeight);
    }

    /// <summary>A slot's rectangle inside its group (<c>melody_app_discovery_batteries_horizontal</c>).</summary>
    public static PopupRect SlotRect(PopupSlot slot) => slot switch
    {
        PopupSlot.Left => new PopupRect(22, GroupImageHeight, SlotWidth, SlotHeight),
        PopupSlot.Center => new PopupRect(46.5, GroupImageHeight, SlotWidth, SlotHeight),
        _ => new PopupRect(GroupWidth - 20 - SlotWidth, GroupImageHeight, SlotWidth, SlotHeight),
    };

    /// <summary>Android gravity → horizontal edge (LEFT 3 / START, RIGHT 5 / END, CENTER_HORIZONTAL 1).</summary>
    private static PopupEdge HorizontalEdge(int gravity) => (gravity & 0x07) switch
    {
        0x05 => PopupEdge.End,
        0x01 => PopupEdge.Center,
        _ => PopupEdge.Start,
    };

    /// <summary>Android gravity → vertical edge (TOP 48, BOTTOM 80, CENTER_VERTICAL 16).</summary>
    private static PopupEdge VerticalEdge(int gravity) => (gravity & 0x70) switch
    {
        0x50 => PopupEdge.End,
        0x10 => PopupEdge.Center,
        _ => PopupEdge.Start,
    };

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : null;

    private static double? Number(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>A day colour and the first night alternative that is set (HeyMelody accepts both spellings).</summary>
    private static PopupColor Colour(JsonElement parent, string day, params string[] night)
    {
        string? n = null;
        foreach (string name in night) n ??= ValidColour(String(parent, name));
        return new PopupColor(ValidColour(String(parent, day)), n);
    }

    private static string? ValidColour(string? text) => ParseArgb(text) is null ? null : text;

    /// <summary>Android <c>Color.parseColor</c> for #RRGGBB / #AARRGGBB; null otherwise.</summary>
    public static uint? ParseArgb(string? text)
    {
        if (text is null || !text.StartsWith('#')) return null;
        string hex = text[1..];
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return null;
        return hex.Length switch
        {
            6 => 0xFF000000u | value,
            8 => value,
            _ => null,
        };
    }
}
