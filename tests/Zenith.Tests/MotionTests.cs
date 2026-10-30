using Zenith.Gui.Graphics;
using Zenith.Gui.Motion;

namespace Zenith.Tests;

public sealed class MotionTests
{
    private static int[] Filled(int count, int color)
    {
        var pixels = new int[count]; System.Array.Fill(pixels, color); return pixels;
    }

    [Fact]
    public void CurveStaysInRangeAndReachesTheEnds()
    {
        Assert.Equal(0, MotionCurve.Linear(-5, 100));
        Assert.Equal(512, MotionCurve.Linear(50, 100));
        Assert.Equal(MotionCurve.One, MotionCurve.Linear(100, 100));
        Assert.Equal(MotionCurve.One, MotionCurve.Linear(0, 0));
        Assert.Equal(0, MotionCurve.EaseOut(0));
        Assert.Equal(MotionCurve.One, MotionCurve.EaseOut(MotionCurve.One));
        int previous = 0;
        for (int i = 0; i <= MotionCurve.One; i += 8)
        {
            int eased = MotionCurve.EaseOut(i);
            Assert.True(eased >= previous && eased >= i);
            previous = eased;
        }
        Assert.Equal(50, MotionCurve.Lerp(100, 0, 512));
    }

    [Fact]
    public void CaptureAndOpaqueDrawRoundTripClippedRectangles()
    {
        var source = new int[16];
        for (int i = 0; i < 16; i++) { source[i] = i + 1; }
        var layer = new PixelLayer();
        layer.Capture(source, 4, 4, new Rect(-1, 2, 3, 9)); // Clipped to x 0..1, y 2..3.
        Assert.Equal((2, 2), (layer.Width, layer.Height));
        var target = new int[16];
        layer.Draw(target, 4, 4, new Rect(3, 3, 2, 2), 255); // Only the first pixel lands inside.
        Assert.Equal(9, target[15]);
        Assert.Single(System.Array.FindAll(target, p => p != 0));
    }

    [Fact]
    public void DrawScalesAndBlendsWithoutLeavingTheDestination()
    {
        var layer = new PixelLayer();
        layer.Capture(new[] { 1, 2, 3, 4 }, 2, 2, new Rect(0, 0, 2, 2));
        var scaled = new int[16];
        layer.Draw(scaled, 4, 4, new Rect(0, 0, 4, 4), 255);
        Assert.Equal(new[] { 1, 1, 2, 2, 1, 1, 2, 2, 3, 3, 4, 4, 3, 3, 4, 4 }, scaled);

        layer.Capture(Filled(4, unchecked((int)0xffffffff)), 2, 2, new Rect(0, 0, 2, 2));
        var faded = Filled(4, unchecked((int)0xff000000));
        layer.Draw(faded, 2, 2, new Rect(0, 0, 2, 2), 128);
        uint pixel = (uint)faded[0];
        Assert.Equal(0xffu, pixel >> 24);
        Assert.InRange(pixel & 0xff, 126u, 130u);
        var hidden = Filled(4, 7);
        layer.Draw(hidden, 2, 2, new Rect(0, 0, 2, 2), 0);
        layer.Draw(hidden, 2, 2, new Rect(-9, -9, 2, 2), 255);
        layer.Draw(hidden, 2, 2, new Rect(0, 0, 0, 0), 255);
        Assert.All(hidden, p => Assert.Equal(7, p));
    }

    private static (MotionPlayer Player, int[] Frame) Started(long duration = 100)
    {
        var layer = new PixelLayer();
        layer.Capture(Filled(4, 5), 2, 2, new Rect(0, 0, 2, 2));
        var player = new MotionPlayer();
        player.Begin(Filled(4, 9), layer, new Rect(0, 0, 2, 2), new Rect(0, 0, 2, 2), 255, 255, 1000, duration);
        return (player, new int[4]);
    }

    [Fact]
    public void PlayerDrawsUntilTheDurationEnds()
    {
        var (player, frame) = Started();
        Assert.True(player.Render(frame, 2, 2, 1050));
        Assert.All(frame, p => Assert.Equal(5, p));
        player.RecordFrame(1000);
        Assert.True(player.Active);
        Assert.False(player.Render(frame, 2, 2, 1100));
        Assert.False(player.Active);
        Assert.Equal(MotionEnd.Finished, player.LastEnd);
        Assert.Equal(1, player.Frames);
        Assert.False(player.Render(frame, 2, 2, 1200));
    }

    [Fact]
    public void RepeatedSlowFramesAndInputEndTheAnimation()
    {
        var (player, _) = Started();
        player.RecordFrame(MotionPlayer.BudgetMicros + 1);
        player.RecordFrame(1);
        player.RecordFrame(MotionPlayer.BudgetMicros + 1);
        Assert.True(player.Active); // Slow frames must be consecutive.
        player.RecordFrame(MotionPlayer.BudgetMicros + 1);
        Assert.False(player.Active);
        Assert.Equal(MotionEnd.OverBudget, player.LastEnd);
        Assert.Equal(MotionPlayer.BudgetMicros + 1, player.WorstFrameMicros);

        (player, _) = Started();
        player.Interrupt();
        Assert.Equal(MotionEnd.Interrupted, player.LastEnd);
        player.Interrupt();
        Assert.Equal(MotionEnd.Interrupted, player.LastEnd);

        (player, _) = Started(0); // Reduced motion: never animates.
        Assert.False(player.Active);
        Assert.Equal(MotionEnd.Finished, player.LastEnd);
    }
}
