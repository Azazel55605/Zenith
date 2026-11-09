using System;

namespace Zenith.Gui.Graphics;

/// <summary>The pointer arrow rendered from its outline at any scale, for the device pointer and
/// for scaled frames. Pure: host-tested.</summary>
internal static class PointerImage
{
    // The same arrow as an outline in logical pixels, tip at (0, 0): rendered at any scale.
    private static readonly float[] s_outline = { 0f, 0f, 12f, 11.6f, 7.8f, 11.6f, 10.9f, 18f, 7.4f, 19.6f, 4.3f, 12.9f, 0f, 16.8f };

    /// <summary>The arrow at <paramref name="percent"/> of its logical size, premultiplied ARGB, hotspot at (0, 0):
    /// anti-aliased from its outline (4x4 samples a pixel), a one-logical-pixel border and a soft shadow.</summary>
    public static uint[] Render(int percent, out int width, out int height)
    {
        float s = Math.Clamp(percent, 100, 300) / 100f;
        width = Math.Min(64, (int)MathF.Ceiling(14f * s) + 1);
        height = Math.Min(64, (int)MathF.Ceiling(21f * s) + 1);
        var image = new uint[width * height];
        for (int py = 0; py < height; py++)
        for (int px = 0; px < width; px++)
        {
            int border = 0, fill = 0, shadow = 0;
            for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                float x = (px + (sx + 0.5f) / 4f) / s, y = (py + (sy + 0.5f) / 4f) / s;
                float d = Inside(x, y);
                if (d >= 1f) { fill++; } else if (d >= 0f) { border++; }
                if (Inside(x - 1f, y - 2f) >= 0f) { shadow++; }
            }
            // Premultiplied: border and fill over the shadow (black at a quarter).
            int a = (border + fill) * 255 / 16, sa = shadow * 64 / 16;
            int alpha = a + sa * (255 - a) / 255;
            int r = (fill * 255 + border * 0x10) / 16;
            int g = (fill * 255 + border * 0x13) / 16;
            int b = (fill * 255 + border * 0x20) / 16;
            image[py * width + px] = (uint)alpha << 24 | (uint)Math.Min(r, alpha) << 16 | (uint)Math.Min(g, alpha) << 8 | (uint)Math.Min(b, alpha);
        }
        return image;
    }

    // Distance to the nearest edge when (x, y) is inside the outline, -1 outside.
    private static float Inside(float x, float y)
    {
        bool inside = false; float nearest = float.MaxValue;
        float[] p = s_outline; int n = p.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = p[2 * i], yi = p[2 * i + 1], xj = p[2 * j], yj = p[2 * j + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) { inside = !inside; }
            float dx = xj - xi, dy = yj - yi, t = Math.Clamp(((x - xi) * dx + (y - yi) * dy) / (dx * dx + dy * dy), 0f, 1f);
            float ex = xi + t * dx - x, ey = yi + t * dy - y;
            nearest = Math.Min(nearest, ex * ex + ey * ey);
        }
        return inside ? MathF.Sqrt(nearest) : -1f;
    }

}
