using CopilotSessionTray.Core.Models;
using CopilotSessionTray.Core.Services;

namespace CopilotSessionTray.Core.Tests.Services;

public sealed class StickerLayoutTests
{
    private static readonly StickerRect Screen = new(0, 0, 1920, 1040);

    private static StickerRect Slot(double x, double y) => new(x, y, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight);

    [Fact]
    public void FindSlot_StartsAtTheTopLeftMargin_WhenNothingIsInTheWay()
    {
        var slot = StickerLayout.FindSlot(Screen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, []);

        Assert.Equal(Slot(8, 8), slot);
    }

    [Fact]
    public void FindSlot_UsesTheWorkAreasOwnTopLeft_NotTheScreensOrigin()
    {
        var workArea = new StickerRect(0, 40, 1920, 1000);

        var slot = StickerLayout.FindSlot(workArea, 200, 100, []);

        Assert.Equal(new StickerRect(8, 48, 200, 100), slot);
    }

    [Fact]
    public void FindSlot_StacksBelowOccupiedStickers_WithAGap()
    {
        var slot = StickerLayout.FindSlot(Screen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, [Slot(8, 8)]);

        Assert.Equal(Slot(8, 8 + StickerLayout.DefaultHeight + StickerLayout.Gap), slot);
    }

    [Fact]
    public void FindSlot_FillsTheGapLeftByAHiddenSticker()
    {
        var below = Slot(8, 8 + StickerLayout.DefaultHeight + StickerLayout.Gap);

        var slot = StickerLayout.FindSlot(Screen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, [below]);

        Assert.Equal(Slot(8, 8), slot);
    }

    [Fact]
    public void FindSlot_WrapsToANewColumn_WhenTheColumnIsFull()
    {
        var shortScreen = new StickerRect(0, 0, 1920, 300);
        var first = StickerLayout.FindSlot(shortScreen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, []);
        var second = StickerLayout.FindSlot(shortScreen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, [first]);

        var third = StickerLayout.FindSlot(shortScreen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, [first, second]);

        Assert.Equal(Slot(8, 140), second);
        Assert.Equal(Slot(8 + StickerLayout.DefaultWidth + StickerLayout.Gap, 8), third);
    }

    [Fact]
    public void FindSlot_StepsAroundAStickerTheUserDraggedIntoTheWay()
    {
        var dragged = new StickerRect(8, 60, 100, 50);

        var slot = StickerLayout.FindSlot(Screen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, [dragged]);

        Assert.Equal(Slot(8, 60 + 50 + StickerLayout.Gap), slot);
    }

    [Fact]
    public void FindSlot_NeverOverlapsAnyOccupiedSticker()
    {
        var occupied = new List<StickerRect>();
        for (var i = 0; i < 20; i++)
        {
            var slot = StickerLayout.FindSlot(Screen, StickerLayout.DefaultWidth, StickerLayout.DefaultHeight, occupied);
            Assert.All(occupied, other => Assert.False(Overlaps(slot, other)));
            occupied.Add(slot);
        }
    }

    [Fact]
    public void FindSlot_CascadesFromTheTopLeft_WhenNothingFits()
    {
        var tiny = new StickerRect(0, 0, 200, 150);

        var first = StickerLayout.FindSlot(tiny, 240, 124, []);
        var third = StickerLayout.FindSlot(tiny, 240, 124, [first, first]);

        Assert.Equal(new StickerRect(8, 8, 240, 124), first);
        Assert.Equal(new StickerRect(8 + 48, 8 + 48, 240, 124), third);
    }

    [Theory]
    [InlineData(null, null, 240.0, 124.0)]
    [InlineData(100.0, 40.0, 170.0, 88.0)]
    [InlineData(5000.0, 5000.0, 1920.0, 1040.0)]
    [InlineData(300.0, null, 300.0, 124.0)]
    public void ClampSize_AppliesDefaultsAndKeepsTheSizeWithinLimits(double? width, double? height, double expectedWidth, double expectedHeight)
    {
        var (clampedWidth, clampedHeight) = StickerLayout.ClampSize(width, height, Screen);

        Assert.Equal(expectedWidth, clampedWidth);
        Assert.Equal(expectedHeight, clampedHeight);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(-120, 100, true)]
    [InlineData(-230, 100, false)]
    [InlineData(1680, 100, true)]
    [InlineData(1700, 100, false)]
    [InlineData(100, -1, false)]
    [InlineData(100, 1030, false)]
    [InlineData(100, 1010, true)]
    public void IsReachable_RequiresTheTopRowAndTheCloseCornerToBeOnScreen(double left, double top, bool expected)
    {
        var sticker = new StickerRect(left, top, 240, 124);

        Assert.Equal(expected, StickerLayout.IsReachable(Screen, sticker));
    }

    [Fact]
    public void IsReachable_AllowsAStickerOnASecondMonitorToTheLeft()
    {
        var bothScreens = new StickerRect(-1920, 0, 3840, 1080);

        Assert.True(StickerLayout.IsReachable(bothScreens, new StickerRect(-1500, 200, 240, 124)));
    }

    [Theory]
    [InlineData(240.0, 124.0, 1.0)]
    [InlineData(480.0, 248.0, 2.0)]
    [InlineData(360.0, 186.0, 1.5)]
    [InlineData(720.0, 372.0, 3.0)]
    [InlineData(216.0, 111.6, 0.9)]
    [InlineData(480.0, 124.0, 1.333333)]
    [InlineData(240.0, 248.0, 1.333333)]
    [InlineData(249.0, 186.0, 1.226601)]
    public void FontScale_FollowsTheStickerSize_AndLeansTowardItsTighterSide(double width, double height, double expected)
    {
        Assert.Equal(expected, StickerLayout.FontScale(width, height), precision: 6);
    }

    [Theory]
    [InlineData(170.0, 88.0)]
    [InlineData(170.0, 124.0)]
    [InlineData(240.0, 88.0)]
    [InlineData(100.0, 40.0)]
    public void FontScale_NeverDropsBelowTheFloor(double width, double height)
    {
        Assert.Equal(0.85, StickerLayout.FontScale(width, height));
        Assert.Equal(0.85, StickerLayout.MinFontScale);
    }

    [Theory]
    [InlineData(double.NaN, 124.0)]
    [InlineData(240.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 124.0)]
    [InlineData(240.0, double.NegativeInfinity)]
    [InlineData(0.0, 124.0)]
    [InlineData(240.0, 0.0)]
    [InlineData(-240.0, 124.0)]
    [InlineData(240.0, -124.0)]
    public void FontScale_IsNeutral_ForSizesThatCannotBeMeasured(double width, double height)
    {
        Assert.Equal(1.0, StickerLayout.FontScale(width, height));
    }

    [Fact]
    public void FontScale_NeverShrinksTheText_WhenAStickerGrowsInEitherDirection()
    {
        for (var width = StickerLayout.MinWidth; width <= 960; width += 10)
        {
            for (var height = StickerLayout.MinHeight; height <= 520; height += 8)
            {
                var scale = StickerLayout.FontScale(width, height);
                Assert.True(StickerLayout.FontScale(width + 10, height) >= scale, $"wider than {width}x{height}");
                Assert.True(StickerLayout.FontScale(width, height + 8) >= scale, $"taller than {width}x{height}");
            }
        }
    }

    [Fact]
    public void FontScale_StaysBelowTwiceTheTighterSidesGrowth_SoALopsidedStickerKeepsRoomForItsText()
    {
        Assert.InRange(StickerLayout.FontScale(2400, 124), 1.8, 2.0);
        Assert.InRange(StickerLayout.FontScale(240, 1240), 1.8, 2.0);
    }

    private static bool Overlaps(StickerRect a, StickerRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
