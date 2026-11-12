using System;
using Zenith.Core.Configuration;

namespace Zenith.Gui.Graphics;

/// <summary>
/// The desktop background, drawn from the wallpaper setting (Meridian › Style › Wallpaper) for the
/// current light/dark mode and accent. Dynamic wallpapers are the formulas of the accepted concept
/// (docs/design/wallpaper-concept.svg; tools/design/wallpaper-concept.py holds the same math in
/// numpy); solid ones are a fill. The desktop re-renders it when <see cref="Key"/> changes, so a new
/// mode, accent or choice shows at once.
/// </summary>
internal static class Wallpaper
{
    private struct Color
    {
        public float R, G, B;
        public Color(float r, float g, float b) { R = r; G = g; B = b; }
        public static Color Of(uint rgb) => new((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        public static Color Mix(Color a, Color b, float t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        }
        public int Pixel => unchecked((int)0xFF000000) | (Clamp(R) << 16) | (Clamp(G) << 8) | Clamp(B);
    }

    /// <summary>The concept's palettes: base top and bottom, second and third glow colors, line ink.</summary>
    private readonly struct Palette
    {
        public readonly Color Top, Bottom, Accent, Second, Third, Ink;
        public readonly bool Light;
        public Palette(bool light, uint accent)
        {
            Light = light; Accent = Color.Of(accent);
            Top = Color.Of(light ? 0xF8F4ECU : 0x1B2036U); Bottom = Color.Of(light ? 0xEDE5D6U : 0x0E1018U);
            Second = Color.Of(light ? 0xE9A87AU : 0x3FB6B2U); Third = Color.Of(light ? 0x7042BEU : 0xB6A0FFU);
            Ink = Color.Of(light ? 0x5D564BU : 0xC9D3FFU);
        }
    }

    private static readonly Color Black = new(0, 0, 0);

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
        string name = choice < SettingsService.WallpaperNames.Length ? SettingsService.WallpaperNames[choice] : "zenith";
        int[] pixels = target.Pixels;
        if (name.StartsWith("solid-", StringComparison.Ordinal))
        {
            uint fill = name switch
            {
                "solid-graphite" => 0x202125, "solid-midnight" => 0x1B2036, "solid-forest" => 0x243B36, "solid-plum" => 0x3A2433,
                "solid-cream" => 0xF8F4EC, "solid-mist" => 0xE8EEF6, "solid-sand" => 0xEDE5D6, _ => accent,
            };
            Array.Fill(pixels, unchecked((int)(0xFF000000 | fill)));
            return;
        }
        var p = new Palette(light, accent);
        int w = target.Width, h = target.Height;
        float aspect = (float)w / Math.Max(1, h);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int y = 0; y < h; y++)
        {
            float v = (float)y / h;
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / w;
                Color c = name switch { "dunes" => Dunes(p, u, v, aspect), "aurora" => Aurora(p, u, v), "contour" => Contour(p, u, v, aspect), _ => Zenith(p, u, v, aspect) };
                pixels[y * w + x] = c.Pixel;
            }
        }
        long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        global::Zenith.Core.Log.Write("wallpaper", name + (light ? " light " : " dark ") + w + "x" + h + " in " + ms + " ms");
    }

    /// <summary>A diagonal gradient with an accent glow at the upper left and a second glow at the lower right.</summary>
    private static Color Zenith(in Palette p, float u, float v, float aspect)
    {
        float a = p.Light ? 0.28f : 0.55f;
        Color c = Color.Mix(p.Top, p.Bottom, u * 0.45f + v * 0.75f);
        c = Color.Mix(c, p.Accent, Glow(u, v, 0.2f, 0.22f, 0.95f, aspect) * a);
        return Color.Mix(c, p.Second, Glow(u, v, 0.9f, 0.88f, 0.8f, aspect) * a * 0.85f);
    }

    /// <summary>Five layered ridges, lighter toward the horizon, each with a soft rim of light.</summary>
    private static Color Dunes(in Palette p, float u, float v, float aspect)
    {
        bool light = p.Light;
        Color sky = Color.Mix(p.Top, Color.Mix(p.Top, p.Accent, light ? 0.20f : 0.45f), Clamp01(1 - v * 1.7f));
        sky = Color.Mix(sky, p.Second, Glow(u, v, 0.70f, 0.40f, 0.60f, aspect) * (light ? 0.40f : 0.50f));
        Color near = light ? Color.Mix(p.Bottom, p.Second, 0.45f) : Color.Mix(p.Bottom, p.Accent, 0.10f);
        Color far = light ? Color.Mix(p.Bottom, p.Top, 0.55f) : Color.Mix(p.Top, p.Accent, 0.38f);
        Color rim = light ? p.Top : Color.Mix(p.Second, p.Top, 0.2f);
        Color result = sky;
        for (int k = 0; k < 5; k++)
        {
            float ridge = 0.46f + k * 0.115f + 0.05f * MathF.Sin(u * (2.6f + k * 0.9f) + k * 2.3f) + 0.022f * MathF.Sin(u * (6.1f + k * 1.3f) + k * 0.7f);
            float depth = v - ridge;
            if (depth <= 0) { continue; }
            Color shade = Color.Mix(far, near, k / 4f);
            Color body = Color.Mix(shade, rim, Clamp01(1 - depth * 14) * (light ? 0.55f : 0.42f));
            result = Color.Mix(body, Color.Mix(shade, light ? p.Ink : Black, 0.5f), Clamp01(depth * 3) * 0.35f);
        }
        return result;
    }

    /// <summary>Three curtains of light, each with a bright, wavy lower hem and rays fading upward.</summary>
    private static Color Aurora(in Palette p, float u, float v)
    {
        Color c = Color.Mix(p.Top, p.Bottom, v * 1.1f);
        if (!p.Light) { c = Color.Mix(c, Black, Clamp01(v - 0.55f) * 0.6f); }
        c = Curtain(c, p.Second, u, v, 0.50f, 5.5f, 0.3f, 0.05f, 0.62f, p.Light);
        c = Curtain(c, p.Accent, u, v, 0.42f, 4.2f, 2.1f, 0.30f, 0.92f, p.Light);
        return Curtain(c, p.Third, u, v, 0.34f, 6.8f, 4.0f, 0.55f, 1.05f, p.Light);
    }

    private static Color Curtain(Color c, Color color, float u, float v, float hem, float freq, float phase, float left, float right, bool light)
    {
        float span = Clamp01((u - left) / 0.12f) * Clamp01((right - u) / 0.12f);
        if (span <= 0) { return c; }
        float h = hem + 0.07f * MathF.Sin(u * freq + phase) + 0.025f * MathF.Sin(u * 17 + phase);
        float curtain = v < h ? MathF.Exp(-(h - v) / 0.16f) : MathF.Exp(-(v - h) / 0.04f) * 0.85f;
        float ray = MathF.Sin(u * 140 + 2.5f * MathF.Sin(u * 9 + phase) + v * 2);
        float rays = 0.62f + 0.38f * ray * ray;
        return Color.Mix(c, color, curtain * span * rays * (light ? 0.42f : 0.80f));
    }

    /// <summary>Topographic lines over a soft gradient; every fourth line is stronger.</summary>
    private static Color Contour(in Palette p, float u, float v, float aspect)
    {
        Color c = Color.Mix(p.Top, p.Bottom, v * 0.9f + u * 0.2f);
        c = Color.Mix(c, p.Accent, Glow(u, v, 0.78f, 0.3f, 0.9f, aspect) * (p.Light ? 0.14f : 0.30f));
        float x = u * aspect * 3.2f, y = v * 3.2f;
        float f = (MathF.Sin(x * 1.3f + MathF.Sin(y * 1.7f)) + MathF.Sin(y * 1.1f - x * 0.6f + 1.3f) + 0.6f * MathF.Sin(x * 2.3f + y * 2.1f + 0.7f)) * 2.2f;
        float nearest = MathF.Round(f), line = Clamp01(1 - MathF.Abs(f - nearest) / 0.045f);
        if (line <= 0) { return c; }
        bool major = ((int)MathF.Abs(nearest)) % 4 == 0;
        float strength = (major ? 0.55f : 0.28f) * (p.Light ? 0.8f : 1.0f);
        return Color.Mix(c, p.Light ? p.Ink : Color.Mix(p.Ink, p.Accent, 0.5f), line * strength);
    }

    private static float Glow(float u, float v, float cx, float cy, float radius, float aspect)
    {
        float dx = (u - cx) * aspect, dy = v - cy;
        float t = 1f - MathF.Sqrt(dx * dx + dy * dy) / radius;
        // Smoothstep: no visible peak at the center, no hard edge at the radius.
        return t <= 0f ? 0f : t * t * (3f - 2f * t);
    }

    private static float Clamp01(float t) => t < 0 ? 0 : t > 1 ? 1 : t;
    private static int Clamp(float v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)v;
}
