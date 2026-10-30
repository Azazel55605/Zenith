using System;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Motion;

/// <summary>A reusable copy of one screen rectangle that can be drawn back scaled and faded.</summary>
internal sealed class PixelLayer
{
    private int[] _pixels = Array.Empty<int>();

    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Capture(int[] source, int sourceWidth, int sourceHeight, Rect area)
    {
        Rect c = area.Intersect(new Rect(0, 0, sourceWidth, sourceHeight));
        Width = c.W; Height = c.H;
        if (_pixels.Length < c.W * c.H) { _pixels = new int[c.W * c.H]; }
        for (int y = 0; y < c.H; y++)
        {
            Array.Copy(source, (c.Y + y) * sourceWidth + c.X, _pixels, y * c.W, c.W);
        }
    }

    /// <summary>Draws the layer stretched over <paramref name="target"/> (nearest neighbour), clipped to the destination.</summary>
    public void Draw(int[] destination, int destinationWidth, int destinationHeight, Rect target, int alpha)
    {
        if (target.IsEmpty || Width == 0 || Height == 0 || alpha <= 0) { return; }
        Rect c = target.Intersect(new Rect(0, 0, destinationWidth, destinationHeight));
        if (c.IsEmpty) { return; }
        bool opaque = alpha >= 255, unscaled = target.W == Width && target.H == Height;
        uint a = (uint)Math.Min(alpha, 255), inverse = 255 - a;
        long stepX = ((long)Width << 16) / target.W;
        long startX = (c.X - target.X) * stepX;
        for (int y = c.Y; y < c.Bottom; y++)
        {
            int row = (int)((long)(y - target.Y) * Height / target.H) * Width;
            int at = y * destinationWidth + c.X;
            if (opaque && unscaled)
            {
                Array.Copy(_pixels, row + (c.X - target.X), destination, at, c.W);
                continue;
            }
            long fx = startX;
            for (int x = 0; x < c.W; x++, at++, fx += stepX)
            {
                uint s = (uint)_pixels[row + (int)(fx >> 16)];
                if (opaque) { destination[at] = (int)s; continue; }
                uint d = (uint)destination[at];
                uint rb = (((s & 0xff00ff) * a + (d & 0xff00ff) * inverse + 0x800080) >> 8) & 0xff00ff;
                uint g = (((s & 0xff00) * a + (d & 0xff00) * inverse + 0x8000) >> 8) & 0xff00;
                destination[at] = (int)(0xff000000 | rb | g);
            }
        }
    }
}
