using Zenith.Gui.Graphics;
using Zenith.Gui.Motion;
using Zenith.Gui.Shell;

namespace Zenith.Tests;

public class TaskbarTests
{
    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(0, true, false, true)]
    [InlineData(0, false, true, false)]
    [InlineData(1, true, false, true)]
    [InlineData(1, false, true, false)]
    [InlineData(2, true, false, true)]
    [InlineData(3, false, false, true)]
    [InlineData(3, true, false, false)]
    [InlineData(3, false, true, false)]
    [InlineData(4, false, false, false)]
    public void ModeDecidesVisibility(int mode, bool maximized, bool fullscreen, bool shown)
        => Assert.Equal(shown, TaskbarPolicy.Shown((TaskbarMode)mode, fullscreen, maximized, false, false));

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(0, true, false, true)]
    [InlineData(0, false, true, true)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, false, true)]
    [InlineData(3, true, false, false)]
    [InlineData(4, true, false, false)]
    public void ModeDecidesShape(int mode, bool maximized, bool fullscreen, bool docked)
        => Assert.Equal(docked, TaskbarPolicy.Docked((TaskbarMode)mode, fullscreen, maximized));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PointerOrPopupAlwaysBringsItBack(bool pointer, bool popup)
    {
        foreach (TaskbarMode mode in System.Enum.GetValues<TaskbarMode>())
        { Assert.True(TaskbarPolicy.Shown(mode, true, true, pointer, popup)); }
    }

    [Theory]
    [InlineData(0, 48)]
    [InlineData(1, 64)]
    [InlineData(2, 48)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    public void DockedAndFloatingKeepTheirOwnSpace(int mode, int rows)
        => Assert.Equal(rows, TaskbarPolicy.Reserved((TaskbarMode)mode, 48, 8));

    [Fact]
    public void PillsKeepNaturalWidthWhenTheyFit()
        => Assert.Equal(new[] { 100, 150 }, TaskbarLayout.Fit(new[] { 100, 150 }, 254));

    [Fact]
    public void WidestPillsShrinkFirstAndAllFit()
    {
        int[] widths = TaskbarLayout.Fit(new[] { 100, 200, 200 }, 408);
        Assert.Equal(100, widths[0]);
        Assert.Equal(150, widths[1]);
        Assert.Equal(150, widths[2]);
    }

    [Theory]
    [InlineData(5, 1000, 5)]
    [InlineData(10, 1000, 7)]
    [InlineData(3, 40, 0)]
    [InlineData(0, 500, 0)]
    public void CountLeavesRoomForOverflow(int windows, int available, int expected)
    {
        int count = TaskbarLayout.Count(windows, available);
        Assert.Equal(expected, count);
        if (count < windows)
        { Assert.True(count * (TaskbarLayout.MinimumPill + TaskbarLayout.Gap) + TaskbarLayout.OverflowWidth <= System.Math.Max(available, TaskbarLayout.OverflowWidth)); }
    }

    [Fact]
    public void RoundedLayerLeavesCornersOfTheBase()
    {
        int[] source = new int[100];
        System.Array.Fill(source, 7);
        var layer = new PixelLayer();
        layer.Capture(source, 10, 10, new Rect(0, 0, 10, 10));
        int[] destination = new int[100];
        layer.Draw(destination, 10, 10, new Rect(0, 0, 10, 10), 255, 4);
        Assert.Equal(0, destination[0]);
        Assert.Equal(0, destination[9]);
        Assert.Equal(0, destination[90]);
        Assert.Equal(0, destination[99]);
        Assert.Equal(7, destination[55]);
        Assert.Equal(7, destination[5]);
    }

    [Fact]
    public void OverlayDrawsAboveTheLayerAndMovesWithIt()
    {
        int[] baseImage = new int[100], under = new int[100], over = new int[100], frame = new int[100];
        System.Array.Fill(under, 1); System.Array.Fill(over, 2);
        PixelLayer window = new(), bar = new();
        window.Capture(under, 10, 10, new Rect(0, 0, 10, 10));
        bar.Capture(over, 10, 10, new Rect(0, 0, 10, 2));
        var player = new MotionPlayer();
        player.Begin(baseImage, window, new Rect(0, 0, 10, 10), new Rect(0, 0, 10, 10), 255, 255, 0, 1000);
        player.SetOverlay(bar, new Rect(0, 8, 10, 2), new Rect(0, 10, 10, 2), 0, 0);
        Assert.True(player.Render(frame, 10, 10, 0));
        Assert.Equal(1, frame[75]);
        Assert.Equal(2, frame[85]);
        Assert.True(player.Render(frame, 10, 10, 999));
        Assert.Equal(1, frame[85]);
    }

    [Fact]
    public void CrossfadeEndsExactlyOnEachFrame()
    {
        int[] from = { unchecked((int)0xff102030) }, to = { unchecked((int)0xffa0b0c0) }, output = new int[1];
        PixelBlend.Mix(from, to, output, 0);
        Assert.Equal(from[0], output[0]);
        PixelBlend.Mix(from, to, output, 255);
        Assert.Equal(to[0], output[0]);
    }
}
