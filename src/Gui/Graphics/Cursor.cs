using System;

namespace Zenith.Gui.Graphics;

/// <summary>
/// The pointer: the Meridian cursor set (docs/design/cursor-concept.svg), Light or Dark from
/// Meridian › Style › Cursors, in the shape the desktop asks for (<see cref="Shape"/>). Drawn at
/// 24 logical pixels into frames (the display scaler enlarges it with everything else), or by the
/// device at the scale's exact size (24 to 48 px) when it draws the pointer itself.
/// </summary>
internal static class Cursor
{
    public const int Size = 24;
    public static CursorShape Shape { get; set; }
    public static int Variant
    {
        get { uint v = Zenith.Core.Configuration.SystemConfiguration.Settings?.Current.Cursor ?? 0; return v < CursorImages.Variants ? (int)v : 0; }
    }

    /// <summary>The pixels any shape (shadow included) can touch when drawn at (<paramref name="x"/>,
    /// <paramref name="y"/>): the same for every shape, so a shape change repaints its old pixels.
    /// Empty while the device draws the pointer itself: frames never contain it then.</summary>
    public static Rect Bounds(int x, int y) => HardwareCursor.Active ? new Rect(x, y, 0, 0) : new Rect(x - 12, y - 12, Size + 12, Size + 12);

    /// <summary>Draws the pointer into an on-screen frame, or moves the device's pointer to it.</summary>
    public static void Draw(Surface surface, int x, int y)
    {
        if (HardwareCursor.Show()) { return; }
        uint[] image = CursorImages.Get(Variant, Shape, Size, out int hotX, out int hotY);
        int left = x - hotX, top = y - hotY;
        Rect clip = new Rect(left, top, Size, Size).Intersect(surface.Clip);
        int[] pixels = surface.Pixels; int width = surface.Width;
        for (int py = clip.Y; py < clip.Bottom; py++)
        {
            for (int px = clip.X; px < clip.Right; px++)
            {
                uint src = image[(py - top) * Size + (px - left)], a = src >> 24;
                if (a == 0) { continue; }
                int at = py * width + px;
                if (a == 255) { pixels[at] = unchecked((int)(src | 0xFF000000)); continue; }
                uint dst = unchecked((uint)pixels[at]), k = 255 - a;
                uint r = ((src >> 16) & 255) + (((dst >> 16) & 255) * k + 127) / 255;
                uint g = ((src >> 8) & 255) + (((dst >> 8) & 255) * k + 127) / 255;
                uint b = (src & 255) + ((dst & 255) * k + 127) / 255;
                pixels[at] = unchecked((int)(0xFF000000 | Math.Min(r, 255u) << 16 | Math.Min(g, 255u) << 8 | Math.Min(b, 255u)));
            }
        }
    }
}
