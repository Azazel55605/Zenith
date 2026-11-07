using System;
using Xunit;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

public class ShadowMaskTests
{
    // Every pixel of every shadow the desktop draws must match the old per-pixel formula.
    [Theory]
    [InlineData(12, 20, 90)] [InlineData(12, 20, 45)] [InlineData(12, 18, 100)] [InlineData(12, 14, 70)] [InlineData(0, 20, 90)] [InlineData(3, 1, 255)]
    public void TableMatchesThePerPixelFormulaExactly(int radius, int size, int alpha)
    {
        foreach (var (x, y, w, h) in new[] { (100, 80, 640, 480), (13, 7, 641, 481), (0, 0, 1, 1), (-30, -9, 299, 17), (5, 5, 2 * 12 + 1, 300) })
        {
            var s = new Rect(x, y, w, h);
            var mask = ShadowMask.For(s, radius, size, alpha);
            float halfW = s.W / 2f, halfH = s.H / 2f, cx = s.X + halfW, cy = s.Y + halfH;
            for (int py = s.Y - size - 3; py < s.Bottom + size + 3; py++)
            {
                int row = mask.Row(py);
                for (int px = s.X - size - 3; px < s.Right + size + 3; px++)
                {
                    float qx = MathF.Abs(px + 0.5f - cx) - halfW + radius, qy = MathF.Abs(py + 0.5f - cy) - halfH + radius;
                    Assert.Equal(ShadowMask.Exact(qx, qy, radius, size, alpha), mask.Alpha(px, row));
                }
            }
        }
    }
}
