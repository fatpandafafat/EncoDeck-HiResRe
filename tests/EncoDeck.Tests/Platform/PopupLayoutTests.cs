// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Platform.Media;
using EncoDeck.Platform.Resources;

namespace EncoDeck.Tests.Platform;

public sealed class PopupLayoutTests
{
    private static PopupLayout X3 => PopupLayout.Parse(PopupTimelineTests.X3Like)!;
    private static PopupLayout Free4 => PopupLayout.Parse(PopupTimelineTests.Free4Like)!;
    private static PopupLayout X4 => PopupLayout.Parse(PopupTimelineTests.X4Like)!;

    [Fact]
    public void ProductShotPacks_PutTheLevelsUnderTheEarbudsAndTheCase()
    {
        foreach (var layout in new[] { X3, Free4 })
        {
            Assert.Equal(PopupBatteryStyle.Horizontal, layout.BatteryStyle);
            var a = layout.Arrange(80, 75, 60, hasVideo: true);
            Assert.True(a.VideoVisible);
            Assert.False(a.ImagesVisible);
            Assert.Equal(2, a.Groups.Count);
            Assert.Equal(PopupGroup.Buds, a.Groups[0].Group);
            Assert.Equal([(PopupSlot.Left, PopupBattery.Left), (PopupSlot.Right, PopupBattery.Right)], a.Groups[0].Slots);
            Assert.Equal(PopupGroup.Box, a.Groups[1].Group);
            Assert.Equal([(PopupSlot.Center, PopupBattery.Case)], a.Groups[1].Slots);
            Assert.True(layout.ShowTitle);
        }
        Assert.Equal(new PopupColor("#fafafa", "#404040"), X3.Background);
        Assert.Equal("#404040", X3.Background.Pick(dark: true));
        Assert.False(X3.VideoFadesIn);
        Assert.True(Free4.VideoFadesIn);
        Assert.Null(Free4.Background.Day);
    }

    /// <summary>Enco Free4 Dynaudio: the video shows the brand's logo; the earbuds and case are the foreground animation.</summary>
    private const string ForegroundPack = """
        {"videoRes":{"path":"res/video{NIGHT}/boot.mp4"},"discoveryStates":{"connectedState":{"startTime":11800,"endTime":15850,"loopTime":11800,
          "animation":1,"resShowDelay":1000,"resInAnim":"rightSlideIn","autoCrop":true,
          "bgImageRes":{"path":"res/image/connected_background.png"},"fgAnimRes":{"path":"res/image/connected_foreground.webp"},
          "boxImageRes":{"path":"res/image/connected_box.png"},"budsImageRes":{"path":"res/image/connected_buds.png"},
          "batteriesRes":{"textColor":"#FFFFFFFF"}}}}
        """;

    [Fact]
    public void ForegroundAnimationPack_PlaysItOverTheVideoOnceTheLevelsAreKnown()
    {
        var layout = PopupLayout.Parse(ForegroundPack)!;
        Assert.Equal("res/image/connected_foreground.webp", layout.ForegroundAnimation);
        Assert.True(layout.IsCompact);

        var all = layout.Arrange(80, 75, 60, hasVideo: true);
        Assert.True(all.ForegroundVisible);
        Assert.True(all.VideoVisible);
        // The animation draws the earbuds and the case: the stand-in pictures stay hidden.
        Assert.False(all.ImagesVisible);
        Assert.Equal([PopupGroup.Buds, PopupGroup.Box], all.Groups.Select(g => g.Group));

        // Without a pack colour the video plays from the start, and the animation with it.
        Assert.True(layout.Arrange(80, 75, 0, hasVideo: true).ForegroundVisible);
        // Without the video only the case level is known: the pictures, as HeyMelody shows them.
        var caseOnly = layout.Arrange(0, 0, 60, hasVideo: false);
        Assert.False(caseOnly.ForegroundVisible);
        Assert.True(caseOnly.ImagesVisible);

        Assert.False(Free4.Arrange(80, 75, 60, hasVideo: true).ForegroundVisible);
        // connected_foreground_anim: full width, 100 dp high, 34 dp from the top (the 960 × 300 WebP at 3×).
        Assert.Equal(new PopupRect(0, 34, 320, 100), PopupLayout.ForegroundRect);
    }

    [Fact]
    public void GroupsStandOnHeyMelodysGuidelines()
    {
        var buds = PopupLayout.GroupRect(PopupGroup.Buds);
        var box = PopupLayout.GroupRect(PopupGroup.Box);
        Assert.Equal(320 * 0.28, buds.X + buds.Width / 2, 6);
        Assert.Equal(320 * 0.70, box.X + box.Width / 2, 6);
        Assert.Equal(347 * 0.55, buds.Bottom, 6);
        Assert.Equal((130d, 162d), (buds.Width, buds.Height));
        Assert.Equal(new PopupRect(22, 130, 40, 32), PopupLayout.SlotRect(PopupSlot.Left));
        Assert.Equal(new PopupRect(46.5, 130, 40, 32), PopupLayout.SlotRect(PopupSlot.Center));
        Assert.Equal(new PopupRect(70, 130, 40, 32), PopupLayout.SlotRect(PopupSlot.Right));
    }

    [Fact]
    public void BackgroundColourPack_HidesItsVideoUntilAllLevelsAreKnown()
    {
        // Case unknown (in the ears): the picture of the earbuds in the centre with both levels.
        var a = X3.Arrange(80, 75, 0, hasVideo: true);
        Assert.False(a.VideoVisible);
        Assert.True(a.ImagesVisible);
        var centre = Assert.Single(a.Groups);
        Assert.Equal((PopupGroup.Center, PopupImage.Buds), (centre.Group, centre.Image));
        Assert.Equal([(PopupSlot.Left, PopupBattery.Left), (PopupSlot.Right, PopupBattery.Right)], centre.Slots);

        // Case and one earbud: half the earbuds picture and the case picture.
        var b = X3.Arrange(0, 75, 60, hasVideo: true);
        Assert.Equal([PopupImage.BudsRightHalf, PopupImage.Box], b.Groups.Select(g => g.Image));
        Assert.Equal([(PopupSlot.Center, PopupBattery.Right)], b.Groups[0].Slots);

        // Without a background colour the video stays, with the usual groups.
        var c = Free4.Arrange(80, 75, 0, hasVideo: true);
        Assert.True(c.VideoVisible);
        Assert.Equal([PopupGroup.Buds, PopupGroup.Box], c.Groups.Select(g => g.Group));
    }

    [Fact]
    public void ArtworkPack_UsesAColumnAtTheTopRight()
    {
        var x4 = X4;
        Assert.Equal(PopupBatteryStyle.Vertical, x4.BatteryStyle);
        Assert.Equal((PopupEdge.End, PopupEdge.Start), (x4.ColumnHorizontal, x4.ColumnVertical));
        // topMargin / rightMargin 60 px at 3× = 20 dp, like HeyMelody's own column (marginTop 20 dp).
        Assert.Equal(20, x4.ColumnMargin.Top, 6);
        Assert.Equal(20, x4.ColumnMargin.Right, 6);
        Assert.False(x4.ShowTitle);
        var a = x4.Arrange(80, 75, 60, hasVideo: true);
        Assert.Empty(a.Groups);
        Assert.Equal([PopupBattery.Left, PopupBattery.Right, PopupBattery.Case], a.Column);
        Assert.Equal([PopupBattery.Left], x4.Arrange(80, 0, 0, hasVideo: true).Column);
    }

    [Fact]
    public void StyleOne_IsHeyMelodysDefaultColumn()
    {
        var layout = PopupLayout.Parse("""{"discoveryStates":{"connectedState":{"batteryGroupStyle":1,"startTime":0}}}""")!;
        Assert.Equal(PopupBatteryStyle.Vertical, layout.BatteryStyle);
        Assert.Equal((270d, 20d), (layout.ColumnMargin.Left, layout.ColumnMargin.Top));
        Assert.Equal(PopupEdge.Start, layout.ColumnHorizontal);
    }

    [Fact]
    public void PackColours_TakeTheNightVariantInDarkMode()
    {
        var layout = PopupLayout.Parse("""{"discoveryStates":{"connectedState":{"titleRes":{"textColor":"#FF000000","textColorNight":"#FF888888","text":"%s ready"},"batteriesRes":{"textColor":"#123456","foregroundColor":"bad"}}}}""")!;
        Assert.Equal("#FF888888", layout.TitleColor.Pick(dark: true));
        Assert.Equal("#FF000000", layout.TitleColor.Pick(dark: false));
        // No night colour: the day one, as HeyMelody (ZipConfigUtils.getColor).
        Assert.Equal("#123456", layout.BatteryText.Pick(dark: true));
        Assert.Null(layout.BatteryFill.Day);
        Assert.Equal("%s ready", layout.TitleFormat);
        Assert.Equal(0xFF123456u, PopupLayout.ParseArgb("#123456"));
        Assert.Null(PopupLayout.ParseArgb("123456"));
    }

    [Fact]
    public void ProductShotPacks_AreCompact_ArtworkKeepsTheWholeDialog()
    {
        // One title line (26 dp): 190.85 + 19 + 26 + 24 ≈ 260 dp, i.e. about 276 of 368 DIP.
        double x3 = X3.ContentHeight(26);
        Assert.True(X3.IsCompact);
        Assert.InRange(x3 * 368 / 347, 270, 280);
        Assert.Equal(x3, Free4.ContentHeight(26));
        Assert.False(X4.IsCompact);
        Assert.Equal(PopupLayout.Height, X4.ContentHeight(26));
        // Never shorter than 240 of 368 DIP nor taller than the dialog.
        Assert.Equal(PopupLayout.CompactMinHeight, X3.ContentHeight(0));
        Assert.Equal(PopupLayout.Height, X3.ContentHeight(500));
        // No video segment (pictures only): the whole dialog.
        Assert.False(PopupLayout.Default.IsCompact);
    }
}

public sealed class VideoSegmentTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void FramesBeforeTheSeekTarget_AreSkipped()
    {
        var segment = new VideoSegment(Ms(22000), Ms(29930), Ms(22000));
        Assert.Equal(VideoFrameAction.Skip, segment.Classify(Ms(21500), Ms(22000)));
        Assert.Equal(VideoFrameAction.Present, segment.Classify(Ms(21995), Ms(22000)));
        Assert.Equal(VideoFrameAction.Present, segment.Classify(Ms(25000), Ms(22000)));
    }

    [Fact]
    public void FramesPastTheEnd_EndTheSegment()
    {
        var segment = new VideoSegment(Ms(0), Ms(7500));
        Assert.Equal(VideoFrameAction.Present, segment.Classify(Ms(7500), Ms(0)));
        Assert.Equal(VideoFrameAction.Present, segment.Classify(Ms(7505), Ms(0)));
        Assert.Equal(VideoFrameAction.PastEnd, segment.Classify(Ms(7533), Ms(0)));
        Assert.Null(segment.ContinueAfterEnd()); // hold the last frame
        Assert.Equal(VideoFrameAction.Present, VideoSegment.Whole.Classify(Ms(99999), Ms(0)));
    }

    [Fact]
    public void LoopingSegment_ContinuesFromTheLoopPoint()
    {
        var segment = new VideoSegment(Ms(22000), Ms(29930), Ms(23000));
        Assert.True(segment.Loops);
        Assert.Equal(Ms(23000), segment.ContinueAfterEnd());
        // After the loop seek the reader restarts at the previous key frame: skipped up to the target.
        Assert.Equal(VideoFrameAction.Skip, segment.Classify(Ms(22500), Ms(23000)));
        Assert.Equal(Ms(0), VideoSegment.WholeLooping.ContinueAfterEnd());
    }

    [Fact]
    public void FramesKeepTheirSpacingFromTheAnchor()
    {
        Assert.Equal(Ms(1500), VideoSegment.DueAt(Ms(22500), Ms(22000), Ms(1000)));
        // A frame before the anchor is due at once, never in the past.
        Assert.Equal(Ms(1000), VideoSegment.DueAt(Ms(21900), Ms(22000), Ms(1000)));
    }
}
