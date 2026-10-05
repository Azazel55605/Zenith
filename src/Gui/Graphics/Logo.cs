using System;

namespace Zenith.Gui.Graphics;

/// <summary>
/// The Zenith mark: a sun at the top of its arc, above the horizon. Drawn from primitives so it
/// scales to any size (the launcher button, window headers).
/// </summary>
internal static class Logo
{
    public static void Draw(Surface surface, Rect box)
    {
        int size = Math.Min(box.W, box.H);
        int x = box.X + (box.W - size) / 2;
        int y = box.Y + (box.H - size) / 2;

        // Sun: centred, in the upper part of the box.
        int sun = Math.Max(4, size * 9 / 20);
        Rect sunRect = new(x + (size - sun) / 2, y + size / 10, sun, sun);
        surface.FillRoundRect(sunRect.Inflate(Math.Max(1, size / 16)), sun, Theme.AccentSoft);   // glow
        surface.FillRoundRect(sunRect, sun / 2, Theme.Accent);

        // Horizon: a full-width bar near the bottom, and a shorter one beneath it.
        int bar = Math.Max(2, size / 9);
        int horizonY = y + size * 7 / 10;
        surface.FillRoundRect(new Rect(x, horizonY, size, bar), bar / 2, Theme.TextPrimary);
        int shortW = size * 3 / 5;
        surface.FillRoundRect(new Rect(x + (size - shortW) / 2, horizonY + bar * 2, shortW, bar), bar / 2, Theme.TextSecondary);
    }
}
