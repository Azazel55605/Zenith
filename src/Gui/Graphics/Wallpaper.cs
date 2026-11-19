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

    public static void Render(Surface target, uint choice, bool light, uint accent, bool quiet = false)
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
                Color c = name switch
                {
                    "dunes" => Dunes(p, u, v, aspect), "aurora" => Aurora(p, u, v), "contour" => Contour(p, u, v, aspect),
                    "orbits" => Orbits(p, u, v, aspect), "tides" => Tides(p, u, v, aspect), "facets" => Facets(p, u, v, aspect),
                    "nebula" => Nebula(p, u, v, aspect, x, y), _ => Zenith(p, u, v, aspect),
                };
                pixels[y * w + x] = c.Pixel;
            }
        }
        long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (!quiet) { global::Zenith.Core.Log.Write("wallpaper", name + (light ? " light " : " dark ") + w + "x" + h + " in " + ms + " ms"); }
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

    // ---------- The second set (docs/design/wallpapers-concept-2.svg, 2026-11-19).

    /// <summary>A planet glow in the accent with tilted rings around it and small moons on them.</summary>
    private static Color Orbits(in Palette p, float u, float v, float aspect)
    {
        Color c = Color.Mix(p.Top, p.Bottom, v * 0.8f + u * 0.25f);
        float cx = 0.68f, cy = 0.62f;
        c = Color.Mix(c, p.Accent, Glow(u, v, cx, cy, 0.42f, aspect) * (p.Light ? 0.30f : 0.52f));
        // Ring space: rotate -16 degrees around the planet, in height units.
        float dx = (u - cx) * aspect, dy = v - cy;
        const float cos = 0.96126f, sin = -0.27564f;
        float rx = dx * cos + dy * sin, ry = -dx * sin + dy * cos;
        for (int k = 0; k < 6; k++)
        {
            float a = 0.13f + k * 0.11f, b = a * 0.36f;
            float e = MathF.Sqrt(rx * rx / (a * a) + ry * ry / (b * b));
            float line = Clamp01(1 - MathF.Abs(e - 1) * b / 0.0018f);
            if (line > 0) { c = Color.Mix(c, p.Accent, line * (0.75f - k * 0.1f) * (p.Light ? 0.75f : 1f)); }
            // One moon per ring, at a fixed angle.
            float t = 1.1f + k * 0.9f, mx = a * MathF.Cos(t), my = b * MathF.Sin(t);
            float md = MathF.Sqrt((rx - mx) * (rx - mx) + (ry - my) * (ry - my));
            float moon = Clamp01(1 - (md - (k % 2 == 0 ? 0.004f : 0.0055f)) / 0.0015f);
            if (moon > 0) { c = Color.Mix(c, k % 3 == 0 ? p.Third : p.Accent, moon); }
        }
        // The planet: a lit disk with a soft shadow toward the upper left.
        float pd = MathF.Sqrt(dx * dx + dy * dy), body = Clamp01(1 - (pd - 0.05f) / 0.002f);
        if (body > 0)
        {
            float shade = Clamp01(MathF.Sqrt((dx + 0.012f) * (dx + 0.012f) + (dy + 0.014f) * (dy + 0.014f)) / 0.05f);
            Color disk = Color.Mix(Color.Mix(p.Accent, p.Top, p.Light ? 0.0f : 0.1f), p.Bottom, (1 - shade) * 0.35f);
            c = Color.Mix(c, disk, body);
        }
        return c;
    }

    /// <summary>Six wave bands rising from the bottom edge, alternating the accent and its partner hue.</summary>
    private static Color Tides(in Palette p, float u, float v, float aspect)
    {
        Color c = Color.Mix(p.Top, p.Bottom, v);
        c = Color.Mix(c, p.Third, Glow(u, v, 0.25f, 0.25f, 0.35f, aspect) * (p.Light ? 0.22f : 0.34f));
        float x = u * aspect;
        for (int k = 0; k < 6; k++)
        {
            float amp = 0.055f - k * 0.006f, wl = 1.05f - k * 0.07f, ph = k * 0.9f;
            float crest = 0.42f + k * 0.1f + amp * MathF.Sin(x / wl * 2 * MathF.PI + ph);
            float depth = v - crest;
            if (depth < -0.003f) { continue; }
            Color band = k % 2 == 0 ? p.Accent : p.Third;
            float strength = MathF.Min(0.85f, (p.Light ? 0.12f : 0.18f) + k * (p.Light ? 0.1f : 0.12f));
            if (depth >= 0) { c = Color.Mix(c, band, strength); }
            c = Color.Mix(c, p.Accent, Clamp01(1 - MathF.Abs(depth) / 0.0025f) * 0.6f);
        }
        return c;
    }

    /// <summary>A low-poly mesh that brightens toward the accent at the lower right; a fixed seed, so every size
    /// gets the same composition.</summary>
    private static Color Facets(in Palette p, float u, float v, float aspect)
    {
        const int cols = 8, rows = 5;
        float gx = u * cols, gy = v * rows;
        int cx = Math.Min(cols - 1, (int)gx), cy = Math.Min(rows - 1, (int)gy);
        for (int ring = 0; ring < 2; ring++)
        {
            for (int oy = -ring; oy <= ring; oy++)
            {
                for (int ox = -ring; ox <= ring; ox++)
                {
                    if (ring == 1 && ox == 0 && oy == 0) { continue; }
                    int qx = cx + ox, qy = cy + oy;
                    if (qx < 0 || qy < 0 || qx >= cols || qy >= rows) { continue; }
                    var (ax, ay) = Vertex(qx, qy, cols, rows); var (bx, by) = Vertex(qx + 1, qy, cols, rows);
                    var (dx, dy) = Vertex(qx, qy + 1, cols, rows); var (ex, ey) = Vertex(qx + 1, qy + 1, cols, rows);
                    if (Inside(gx, gy, ax, ay, bx, by, ex, ey)) { return Facet(p, (ax + bx + ex) / 3 / cols, (ay + by + ey) / 3 / rows, qx * 31 + qy * 7); }
                    if (Inside(gx, gy, ax, ay, ex, ey, dx, dy)) { return Facet(p, (ax + ex + dx) / 3 / cols, (ay + ey + dy) / 3 / rows, qx * 31 + qy * 7 + 3); }
                }
            }
        }
        return Facet(p, u, v, 0);
    }
    private static (float X, float Y) Vertex(int x, int y, int cols, int rows)
    {
        if (x <= 0 || y <= 0 || x >= cols || y >= rows) { return (x, y); }
        uint h = (uint)(x * 73856093 ^ y * 19349663) * 2654435761u;
        return (x + ((h & 255) / 255f - 0.5f) * 0.5f, y + (((h >> 8) & 255) / 255f - 0.5f) * 0.5f);
    }
    private static bool Inside(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by), d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy), d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }
    private static Color Facet(in Palette p, float u, float v, int seed)
    {
        float jitter = ((uint)(seed * 2654435761u) >> 24) / 255f * 0.12f - 0.06f;
        float t = Clamp01(u * 0.7f + v * 0.5f - 0.25f + jitter) * (p.Light ? 0.55f : 0.85f);
        return Color.Mix(Color.Mix(p.Top, p.Bottom, 0.6f), p.Accent, t);
    }

    /// <summary>Soft clouds in the accent, its partner and blue; stars only in dark mode.</summary>
    private static Color Nebula(in Palette p, float u, float v, float aspect, int x, int y)
    {
        Color c = Color.Mix(p.Top, p.Bottom, v * 0.8f + u * 0.2f);
        float k = p.Light ? 0.6f : 1f;
        c = Color.Mix(c, p.Accent, Glow(u, v, 0.30f, 0.40f, 0.55f, aspect) * 0.55f * k);
        c = Color.Mix(c, p.Third, Glow(u, v, 0.62f, 0.55f, 0.60f, aspect) * 0.45f * k);
        c = Color.Mix(c, Color.Of(p.Light ? 0xB9C6EAU : 0x6F86C8U), Glow(u, v, 0.76f, 0.30f, 0.40f, aspect) * 0.5f * k);
        c = Color.Mix(c, p.Accent, Glow(u, v, 0.45f, 0.78f, 0.42f, aspect) * 0.35f * k);
        // Stars: one in a few 7-pixel cells, at a hashed spot and brightness.
        int sx = x / 7, sy = y / 7;
        uint h = (uint)(sx * 73856093 ^ sy * 19349663) * 2654435761u;
        if ((h & 63) < 3 && (x % 7) == (int)((h >> 8) % 7) && (y % 7) == (int)((h >> 12) % 7))
        {
            float bright = 0.3f + ((h >> 16) & 255) / 255f * 0.6f;
            c = Color.Mix(c, p.Light ? p.Accent : new Color(255, 255, 255), p.Light ? 0.25f : bright);
        }
        return c;
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
