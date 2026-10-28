using System;

namespace Zenith.Gui.Graphics;

/// <summary>Bounded corner-only composition. Captures the actual lower windows/desktop,
/// then masks the completed window, including clients which copy raw pixel rows.
/// No extra full-screen/window framebuffer or allocation per frame.</summary>
internal sealed class RoundedComposition
{
    public const int MaximumRadius = 32;
    private readonly int[] _backdrop = new int[4 * MaximumRadius * MaximumRadius];

    public static int Coverage(int x, int y, int radius)
    {
        if (radius <= 0) { return 255; }
        if (radius > MaximumRadius) { throw new ArgumentOutOfRangeException(nameof(radius)); }
        int hits = 0, r = radius * 8;
        for (int sy = 1; sy < 8; sy += 2)
        {
            for (int sx = 1; sx < 8; sx += 2)
            {
                int dx = r - (x * 8 + sx), dy = r - (y * 8 + sy);
                if (dx * dx + dy * dy <= r * r) { hits++; }
            }
        }
        return (hits * 255 + 8) / 16;
    }

    public void Capture(int[] pixels, int width, int height, Rect bounds, int radius, int corners = 15)
        => Visit(pixels, width, height, bounds, radius, false, corners);

    public void Composite(int[] pixels, int width, int height, Rect bounds, int radius, int corners = 15)
        => Visit(pixels, width, height, bounds, radius, true, corners);

    private void Visit(int[] pixels, int width, int height, Rect bounds, int radius, bool composite, int corners)
    {
        if (width <= 0 || height <= 0 || (long)width * height != pixels.Length)
        { throw new ArgumentException("Invalid composition surface"); }
        radius = Math.Clamp(radius, 0, Math.Min(MaximumRadius, Math.Min(bounds.W, bounds.H) / 2));
        for (int corner = 0; corner < 4; corner++)
        {
            if ((corners & (1 << corner)) == 0) { continue; }
            for (int y = 0; y < radius; y++)
            {
                for (int x = 0; x < radius; x++)
                {
                    long px = (corner & 1) == 0 ? (long)bounds.X + x : (long)bounds.X + bounds.W - 1 - x;
                    long py = (corner & 2) == 0 ? (long)bounds.Y + y : (long)bounds.Y + bounds.H - 1 - y;
                    if (px < 0 || py < 0 || px >= width || py >= height) { continue; }
                    int index = (int)py * width + (int)px;
                    int slot = (corner * MaximumRadius + y) * MaximumRadius + x;
                    if (!composite) { _backdrop[slot] = pixels[index]; continue; }
                    int coverage = Coverage(x, y, radius);
                    pixels[index] = Blend(pixels[index], _backdrop[slot], coverage);
                }
            }
        }
    }

    internal static int Blend(int foreground, int background, int coverage)
    {
        uint result = 0xff000000;
        for (int shift = 0; shift < 24; shift += 8)
        {
            int channel = (((foreground >> shift) & 255) * coverage
                + ((background >> shift) & 255) * (255 - coverage) + 127) / 255;
            result |= (uint)channel << shift;
        }
        return unchecked((int)result);
    }
}
