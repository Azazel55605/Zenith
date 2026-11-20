using System;
using System.Collections.Generic;

namespace Zenith.Gui.Graphics;

/// <summary>The cursor packs (docs/design/cursor-packs-concept.svg, kernel 0.0.72): Meridian Light and Dark are the
/// generated images; Contrast (1.5x, pure black and white, a heavier outline) and Comet (the accent with a white rim)
/// are derived from them at run time, and every pack is scaled to the chosen size (24, 32 or 48 logical pixels) at the
/// display's scale. No extra images in the kernel. Pure: host-tested.</summary>
internal static class CursorPacks
{
    public const int Light = 0, Dark = 1, ContrastWhite = 2, ContrastBlack = 3, Comet = 4, Count = 5;
    public static readonly int[] LogicalSizes = { 24, 32, 48 };

    /// <summary>The pack's on-screen size in logical pixels for a size choice (Contrast is half as large again).</summary>
    public static int Logical(int pack, int sizeChoice)
    {
        int size = LogicalSizes[Math.Clamp(sizeChoice, 0, LogicalSizes.Length - 1)];
        return pack is ContrastWhite or ContrastBlack ? size * 3 / 2 : size;
    }

    private readonly record struct Key(int Pack, CursorShape Shape, int Size, uint Accent);
    private static readonly Dictionary<Key, (uint[] Pixels, int HotX, int HotY)> s_cache = new();
    private static readonly object s_gate = new();

    /// <summary>The image (premultiplied ARGB, <paramref name="size"/> square) and its hotspot.</summary>
    public static uint[] Get(int pack, CursorShape shape, int size, uint accent, out int hotX, out int hotY)
    {
        pack = Math.Clamp(pack, 0, Count - 1); size = Math.Clamp(size, 16, 144);
        if (pack != Comet) { accent = 0; }
        var key = new Key(pack, shape, size, accent);
        lock (s_gate)
        {
            if (s_cache.TryGetValue(key, out var hit)) { hotX = hit.HotX; hotY = hit.HotY; return hit.Pixels; }
            if (s_cache.Count > 96) { s_cache.Clear(); }
        }
        int baseVariant = pack is Dark or ContrastBlack ? CursorImages.Variants - 1 : 0;
        // The generated size nearest at or above the target (the largest when beyond it), then scaled.
        int source = CursorImages.Sizes[^1];
        foreach (int s in CursorImages.Sizes) { if (s >= size) { source = s; break; } }
        uint[] raw = CursorImages.Get(baseVariant, shape, source, out int rawX, out int rawY);
        uint[] pixels = source == size ? (uint[])raw.Clone() : Scale(raw, source, size);
        hotX = (int)Math.Round(rawX * (double)size / source); hotY = (int)Math.Round(rawY * (double)size / source);
        if (pack is ContrastWhite or ContrastBlack) { pixels = Contrast(pixels, size, pack == ContrastWhite); }
        else if (pack == Comet) { Recolor(pixels, accent); }
        lock (s_gate) { s_cache[key] = (pixels, hotX, hotY); }
        return pixels;
    }

    /// <summary>Bilinear scaling of premultiplied ARGB (no fringes, since the channels are premultiplied).</summary>
    public static uint[] Scale(uint[] src, int from, int to)
    {
        var dst = new uint[to * to];
        double k = (double)from / to;
        for (int y = 0; y < to; y++)
        {
            double sy = (y + 0.5) * k - 0.5; int y0 = (int)Math.Floor(sy); double fy = sy - y0;
            for (int x = 0; x < to; x++)
            {
                double sx = (x + 0.5) * k - 0.5; int x0 = (int)Math.Floor(sx); double fx = sx - x0;
                double a = 0, r = 0, g = 0, b = 0;
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int px = Math.Clamp(x0 + i, 0, from - 1), py = Math.Clamp(y0 + j, 0, from - 1);
                        double w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                        uint p = src[py * from + px];
                        a += (p >> 24) * w; r += ((p >> 16) & 255) * w; g += ((p >> 8) & 255) * w; b += (p & 255) * w;
                    }
                }
                dst[y * to + x] = (uint)Math.Round(a) << 24 | (uint)Math.Round(r) << 16 | (uint)Math.Round(g) << 8 | (uint)Math.Round(b);
            }
        }
        return dst;
    }

    private static int Luma(uint p, uint a) => a == 0 ? 0 : (int)((((p >> 16) & 255) * 30 + ((p >> 8) & 255) * 59 + (p & 255) * 11) / 100 * 255 / a);
    private static bool Saturated(uint p)
    {
        int r = (int)((p >> 16) & 255), g = (int)((p >> 8) & 255), b = (int)(p & 255);
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60;
    }

    /// <summary>Comet: the white body takes the accent, the dark outline turns white; the shadow and the colored parts
    /// (the busy arc, the red of Not allowed) stay as they are.</summary>
    public static void Recolor(uint[] pixels, uint accent)
    {
        uint ar = (accent >> 16) & 255, ag = (accent >> 8) & 255, ab = accent & 255;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i], a = p >> 24;
            if (a < 120 || Saturated(p)) { continue; }   // shadow, edge or a colored detail
            int t = Math.Clamp(Luma(p, a), 0, 255);           // 255 = body (white), 0 = outline (near black)
            uint r = (uint)((ar * t + 255 * (255 - t)) / 255), g = (uint)((ag * t + 255 * (255 - t)) / 255), b = (uint)((ab * t + 255 * (255 - t)) / 255);
            pixels[i] = a << 24 | (r * a / 255) << 16 | (g * a / 255) << 8 | (b * a / 255);
        }
    }

    /// <summary>Contrast: pure body and outline colors, a heavier outline grown outward by size/24 pixels, no shadow.</summary>
    public static uint[] Contrast(uint[] pixels, int size, bool whiteBody)
    {
        uint body = whiteBody ? 0xFFFFFFFFu : 0xFF000000u, line = whiteBody ? 0xFF000000u : 0xFFFFFFFFu;
        var result = new uint[pixels.Length];
        var solid = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i], a = p >> 24;
            if (a < 140) { continue; }                        // shadow and soft edges go
            solid[i] = true;
            if (Saturated(p)) { result[i] = p | 0xFF000000u; continue; }
            int luma = Luma(p, a);
            bool isBody = whiteBody ? luma >= 128 : luma < 128;
            result[i] = isBody ? body : line;
        }
        int grow = Math.Max(1, size / 24);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int at = y * size + x;
                if (solid[at]) { continue; }
                bool near = false;
                for (int dy = -grow; dy <= grow && !near; dy++)
                {
                    for (int dx = -grow; dx <= grow && !near; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size || dx * dx + dy * dy > grow * grow + 1) { continue; }
                        near = solid[ny * size + nx];
                    }
                }
                if (near) { result[at] = line; }
            }
        }
        return result;
    }
}
