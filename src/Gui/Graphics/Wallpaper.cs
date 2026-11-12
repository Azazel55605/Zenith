using System;
using Zenith.Core.Configuration;

namespace Zenith.Gui.Graphics;

/// <summary>
/// The desktop background, drawn from the wallpaper setting (Meridian › Style › Wallpaper) for the
/// current light/dark mode and accent. Dynamic wallpapers are formulas
/// (docs/design/wallpaper-concept.svg, tools/design/wallpaper-concept.py); solid ones are a fill.
/// The desktop re-renders it when <see cref="Key"/> changes, so a new mode, accent or choice shows
/// at once.
/// </summary>
internal static class Wallpaper
{
    // Solid fills by SettingsService.WallpaperNames index (0 = Zenith; the last follows the accent).
    private static readonly uint[] s_solids = { 0, 0x202125, 0x1B2036, 0x243B36, 0x3A2433, 0xF8F4EC, 0xE8EEF6, 0xEDE5D6 };

    public static uint Choice
    {
        get
        {
            uint choice = SystemConfiguration.Settings?.Current.Wallpaper ?? 0;
            return choice < SettingsService.WallpaperNames.Length ? choice : 0;
        }
    }

    /// <summary>Changes whenever the rendered wallpaper would.</summary>
    public static long Key() => ((long)Choice << 40) | ((Theme.IsLight ? 1L : 0L) << 32) | (Theme.Accent & 0xFFFFFF);

    public static void Render(Surface target) => Render(target, Choice, Theme.IsLight, Theme.Accent & 0xFFFFFF);

    public static void Render(Surface target, uint choice, bool light, uint accent)
    {
        int[] pixels = target.Pixels;
        if (choice > 0)
        {
            uint fill = choice < s_solids.Length ? s_solids[choice] : accent;
            Array.Fill(pixels, unchecked((int)(0xFF000000 | fill)));
            return;
        }
        Zenith(target, light, accent);
    }

    /// <summary>A diagonal gradient with an accent glow at the upper left and a second glow at the
    /// lower right: dusk blue in dark mode, cream with a warm glow in light mode.</summary>
    private static void Zenith(Surface target, bool light, uint accent)
    {
        int w = target.Width, h = target.Height;
        uint top = light ? 0xF8F4ECU : 0x1B2036U, bottom = light ? 0xEDE5D6U : 0x0E1018U, second = light ? 0xE9A87AU : 0x3FB6B2U;
        float strength = light ? 0.28f : 0.55f, aspect = (float)w / Math.Max(1, h);
        int[] pixels = target.Pixels;
        for (int y = 0; y < h; y++)
        {
            float v = (float)y / h;
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / w;
                float r = 0, g = 0, b = 0;
                Mix(ref r, ref g, ref b, top, bottom, u * 0.45f + v * 0.75f, true);
                Mix(ref r, ref g, ref b, 0, accent, Glow(u, v, 0.2f, 0.22f, 0.95f, aspect) * strength, false);
                Mix(ref r, ref g, ref b, 0, second, Glow(u, v, 0.9f, 0.88f, 0.8f, aspect) * strength * 0.85f, false);
                pixels[y * w + x] = unchecked((int)0xFF000000) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
            }
        }
    }

    /// <summary>Sets the colour to a..b at t (first), or moves the current colour toward b by t.</summary>
    private static void Mix(ref float r, ref float g, ref float b, uint a, uint c, float t, bool first)
    {
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        float ar = first ? (a >> 16) & 255 : r, ag = first ? (a >> 8) & 255 : g, ab = first ? a & 255 : b;
        r = ar + (((c >> 16) & 255) - ar) * t;
        g = ag + (((c >> 8) & 255) - ag) * t;
        b = ab + ((c & 255) - ab) * t;
    }

    private static float Glow(float u, float v, float cx, float cy, float radius, float aspect)
    {
        float dx = (u - cx) * aspect, dy = v - cy;
        float t = 1f - MathF.Sqrt(dx * dx + dy * dy) / radius;
        // Smoothstep: no visible peak at the center, no hard edge at the radius.
        return t <= 0f ? 0f : t * t * (3f - 2f * t);
    }

    private static int Clamp(float v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)v;
}
