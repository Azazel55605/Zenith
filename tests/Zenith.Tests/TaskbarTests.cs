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
    [InlineData(1, false, false, true)]
    [InlineData(1, true, false, false)]
    [InlineData(1, false, true, false)]
    [InlineData(2, false, false, false)]
    public void ModeDecidesVisibility(int mode, bool maximized, bool fullscreen, bool shown)
        => Assert.Equal(shown, TaskbarPolicy.Shown((TaskbarMode)mode, fullscreen, maximized, false, false));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PointerOrPopupAlwaysBringsItBack(bool pointer, bool popup)
    {
        foreach (TaskbarMode mode in System.Enum.GetValues<TaskbarMode>())
        { Assert.True(TaskbarPolicy.Shown(mode, true, true, pointer, popup)); }
    }

    [Fact]
    public void OnlyAlwaysReservesScreenSpace()
    {
        Assert.True(TaskbarPolicy.Reserves(TaskbarMode.Always));
        Assert.False(TaskbarPolicy.Reserves(TaskbarMode.Maximized));
        Assert.False(TaskbarPolicy.Reserves(TaskbarMode.Auto));
    }

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
        layer.Capture(source, 10, 10, new Rect(0, 0, 10, 10), 4);
        int[] destination = new int[100];
        layer.Draw(destination, 10, 10, new Rect(0, 0, 10, 10), 255);
        Assert.Equal(0, destination[0]);
        Assert.Equal(0, destination[9]);
        Assert.Equal(0, destination[90]);
        Assert.Equal(0, destination[99]);
        Assert.Equal(7, destination[55]);
        Assert.Equal(7, destination[5]);
    }
}
