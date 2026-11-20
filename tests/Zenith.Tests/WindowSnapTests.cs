using Zenith.Gui;
using Zenith.Gui.Graphics;
namespace Zenith.Tests;
public class WindowSnapTests
{
    private static readonly Rect Work = new(0, 0, 1280, 752);   // 1280x800 less a 48 px taskbar
    [Theory]
    [InlineData(0, 400, WindowSnap.Left)]
    [InlineData(1279, 400, WindowSnap.Right)]
    [InlineData(640, 0, WindowSnap.Top)]
    [InlineData(0, 10, WindowSnap.TopLeft)]
    [InlineData(30, 0, WindowSnap.TopLeft)]
    [InlineData(1279, 20, WindowSnap.TopRight)]
    [InlineData(0, 740, WindowSnap.BottomLeft)]
    [InlineData(1278, 700, WindowSnap.BottomRight)]
    [InlineData(640, 400, WindowSnap.None)]
    [InlineData(10, 400, WindowSnap.None)]
    public void ZonesFollowTheEdgesAndCorners(int x, int y, int zone) => Assert.Equal(zone, WindowSnap.ZoneAt(Work, x, y));
    [Fact]
    public void TargetsTileTheWorkAreaWithoutGaps()
    {
        Rect odd = new(0, 0, 1281, 753);
        Rect l = WindowSnap.Target(odd, WindowSnap.Left), r = WindowSnap.Target(odd, WindowSnap.Right);
        Assert.Equal(odd.W, l.W + r.W); Assert.Equal(l.Right, r.X); Assert.Equal(odd.H, l.H);
        Rect tl = WindowSnap.Target(odd, WindowSnap.TopLeft), br = WindowSnap.Target(odd, WindowSnap.BottomRight);
        Assert.Equal(odd.W, tl.W + br.W); Assert.Equal(odd.H, tl.H + br.H); Assert.Equal(tl.Bottom, br.Y);
        Assert.Equal(Work.W, WindowSnap.Target(Work, WindowSnap.Top).W);
        Assert.Equal(0, WindowSnap.Target(Work, WindowSnap.None).W);
    }
    [Fact]
    public void ZonesFollowAWorkAreaThatDoesNotStartAtZero()
    {
        Rect top = new(0, 48, 1280, 752);   // a taskbar at the top
        Assert.Equal(WindowSnap.Top, WindowSnap.ZoneAt(top, 640, 48));
        Assert.Equal(WindowSnap.None, WindowSnap.ZoneAt(top, 640, 60));
        Assert.Equal(48, WindowSnap.Target(top, WindowSnap.Left).Y);
    }
}
