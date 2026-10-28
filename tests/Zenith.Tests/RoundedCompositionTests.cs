using System;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

public class RoundedCompositionTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public void CompletedRectangularClientRestoresBackdropAndBlendsAllFourCorners(int radius)
    {
        const int width = 100, height = 100;
        var pixels = new int[width * height];
        for (int i = 0; i < pixels.Length; i++) { pixels[i] = unchecked((int)0xff000000) | (i * 37 & 0xffffff); }
        var original = (int[])pixels.Clone();
        var mask = new RoundedComposition();
        var b = new Rect(10, 10, 80, 80);
        mask.Capture(pixels, width, height, b, radius);
        // Native clients bypass DrawPoint, exactly as an opaque row-copy does.
        const int client = unchecked((int)0xffeeeeee);
        for (int y = b.Y; y < b.Bottom; y++) { Array.Fill(pixels, client, y * width + b.X, b.W); }
        mask.Composite(pixels, width, height, b, radius);
        int blended = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                if (!b.Contains(x, y)) { Assert.Equal(original[at], pixels[at]); continue; }
                int dx = Math.Min(x - b.X, b.Right - 1 - x), dy = Math.Min(y - b.Y, b.Bottom - 1 - y);
                int coverage = dx < radius && dy < radius ? RoundedComposition.Coverage(dx, dy, radius) : 255;
                Assert.Equal(RoundedComposition.Blend(client, original[at], coverage), pixels[at]);
                if (coverage > 0 && coverage < 255) { blended++; }
            }
        }
        Assert.True(blended > 8); // Coverage edges, not a binary/stair-step mask.
        Assert.Equal(original[b.Y * width + b.X], pixels[b.Y * width + b.X]);
        Assert.Equal(original[(b.Bottom - 1) * width + b.Right - 1], pixels[(b.Bottom - 1) * width + b.Right - 1]);
    }

    [Theory]
    [InlineData(-12, -9)]
    [InlineData(50, 50)]
    public void ClippedWindowsAndRepeatedLayersStayInsideTheFramebuffer(int x, int y)
    {
        var p = new int[64 * 64];Array.Fill(p, unchecked((int)0xff345678));
        var composition = new RoundedComposition();var b = new Rect(x, y, 40, 40);
        for (int frame = 0; frame < 20; frame++)
        {
            composition.Capture(p, 64, 64, b, 12);
            for (int yy = Math.Max(0, y); yy < Math.Min(64, y + 40); yy++)
            { for (int xx = Math.Max(0, x); xx < Math.Min(64, x + 40); xx++) { p[yy * 64 + xx] = unchecked((int)0xffabcdef); } }
            composition.Composite(p, 64, 64, b, 12);
        }
        var copy = (int[])p.Clone();composition.Capture(p, 64, 64, b, 0);composition.Composite(p, 64, 64, b, 0);
        Assert.Equal(copy, p); // Maximized/square windows have no mask.
    }
    [Fact]
    public void InnerContentMaskRoundsOnlyTopLeftAndKeepsOtherCornersSquare()
    {
        var pixels = new int[40 * 40];Array.Fill(pixels, unchecked((int)0xff8899aa));
        var mask = new RoundedComposition();var bounds = new Rect(0, 0, 40, 40);
        mask.Capture(pixels, 40, 40, bounds, 12, 1);Array.Fill(pixels, -1);mask.Composite(pixels, 40, 40, bounds, 12, 1);
        Assert.Equal(unchecked((int)0xff8899aa), pixels[0]);Assert.Equal(-1, pixels[39]);
        Assert.Equal(-1, pixels[39 * 40]);Assert.Equal(-1, pixels[^1]);
    }
}
