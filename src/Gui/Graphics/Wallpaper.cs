using System;

namespace MyOS.Gui.Graphics;

/// <summary>Renders the desktop background once at boot: a dark diagonal gradient with two soft color glows.</summary>
internal static class Wallpaper
{
    public static void Render(Surface target)
    {
        int w = target.Width, h = target.Height;
        float diagonal = w + h;

        // Glow centers and radii, relative to the screen size.
        float g1x = w * 0.18f, g1y = h * 0.22f, g1r = MathF.Max(w, h) * 0.55f;
        float g2x = w * 0.88f, g2y = h * 0.85f, g2r = MathF.Max(w, h) * 0.50f;

        int[] pixels = target.Pixels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float t = (x + y) / diagonal;

                // Base gradient: #0E1120 → #161B30
                float r = 14 + 8 * t;
                float g = 17 + 10 * t;
                float b = 32 + 16 * t;

                float k1 = Falloff(x - g1x, y - g1y, g1r);   // indigo glow
                r += 52 * k1;
                g += 44 * k1;
                b += 120 * k1;

                float k2 = Falloff(x - g2x, y - g2y, g2r);   // teal glow
                r += 6 * k2;
                g += 70 * k2;
                b += 82 * k2;

                pixels[y * w + x] = unchecked((int)0xFF000000) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
            }
        }
    }

    private static float Falloff(float dx, float dy, float radius)
    {
        float t = 1f - MathF.Sqrt(dx * dx + dy * dy) / radius;
        // Smoothstep: no visible peak at the center, no hard edge at the radius.
        return t <= 0f ? 0f : t * t * (3f - 2f * t) * 0.7f;
    }

    private static int Clamp(float v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)v;
}
