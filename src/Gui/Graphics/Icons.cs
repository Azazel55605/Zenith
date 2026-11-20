using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Zenith.Core.Packages;

namespace Zenith.Gui.Graphics;

/// <summary>Package-installed system icons for trusted windows; a missing icon draws nothing.</summary>
internal static class Icons
{
    // Every icon the pack ships fits (about 70); each file is a few KB.
    private const int MaximumCached = 256;
    /// <summary>Sizes zenith-icons renders (tools/build-ui-icons.py SIZES). Others are scaled from the next larger one.</summary>
    private static readonly int[] Stored = { 16, 24, 32, 48 };
    private static readonly Dictionary<string, byte[]> s_files = new();
    // A missing icon is looked up again after a while: the pack may be installed while the desktop runs.
    private static readonly Dictionary<string, long> s_missing = new();

    private static uint s_set;
    /// <summary>Draws from this set instead of the chosen one (Meridian's previews).</summary>
    internal static uint? ForcedSet;
    internal static bool SetInstalled(uint set) => set == 0 || Directory.Exists("/usr/share/zenith/icons/" + Folder(set));
    /// <summary>The icon set (style field 17): 0 Zenith, 1 Bold, 2 Fine. Bold and Fine live in their own folders; an
    /// icon a set lacks comes from the default.</summary>
    private static string Folder(uint set) => set == 1 ? "bold/" : set == 2 ? "fine/" : "";
    private static byte[]? Load(string name)
    {
        uint set = ForcedSet ?? Zenith.Core.Configuration.StyleCatalog.Value(17);
        if (set != s_set) { s_set = set; s_files.Clear(); s_missing.Clear(); }
        if (s_files.TryGetValue(name, out byte[]? known)) { return known; }
        long now = Stopwatch.GetTimestamp();
        if (s_missing.TryGetValue(name, out long since) && now - since < 5 * Stopwatch.Frequency) { return null; }
        if (!IconFile.ValidName(name)) { return null; }
        byte[]? bytes = null;
        try
        {
            foreach (string folder in new[] { Folder(set), "" })
            {
                string path = "/usr/share/zenith/icons/" + folder + name + ".zic";
                if (File.Exists(path) && new FileInfo(path).Length <= IconFile.MaximumBytes) { bytes = File.ReadAllBytes(path); break; }
                if (folder.Length == 0) { break; }
            }
        }
        catch (Exception) { bytes = null; }
        if (bytes is null) { if (s_missing.Count < MaximumCached) { s_missing[name] = now; } return null; }
        s_missing.Remove(name);
        if (s_files.Count < MaximumCached) { s_files[name] = bytes; }
        return bytes;
    }

    /// <summary>The stored size to draw <paramref name="size"/> from: itself, else the smallest larger one, else the largest.</summary>
    internal static int SourceSize(int size)
    {
        foreach (int stored in Stored) { if (stored >= size) { return stored; } }
        return Stored[^1];
    }

    public static bool Draw(Surface surface, string name, int x, int y, int size, uint color)
    {
        byte[]? file = Load(name);
        if (size is < 1 or > 64 || file is null) { return false; }
        Span<byte> coverage = stackalloc byte[size * size];
        if (IconFile.TryFind(file, size, out var runs))
        {
            if (!IconFile.TryDecode(runs, size, coverage)) { return false; }
        }
        else
        {
            int source = SourceSize(size);
            if (!IconFile.TryFind(file, source, out runs)) { return false; }
            Span<byte> full = stackalloc byte[source * source];
            if (!IconFile.TryDecode(runs, source, full)) { return false; }
            Scale(full, source, coverage, size);
        }
        Rect clip = new Rect(x, y, size, size).Intersect(surface.Clip);
        for (int py = clip.Y; py < clip.Bottom; py++)
        {
            for (int px = clip.X; px < clip.Right; px++)
            {
                byte a = coverage[(py - y) * size + (px - x)];
                if (a != 0) { surface.BlendPixel(py * surface.Width + px, color, a); }
            }
        }
        return true;
    }

    /// <summary>Area-averaged resampling of a square coverage mask (exact for down- and upscaling).</summary>
    internal static void Scale(ReadOnlySpan<byte> source, int from, Span<byte> target, int to)
    {
        for (int ty = 0; ty < to; ty++)
        {
            // The target pixel covers [y0, y1) in source pixels, in units of 1/to.
            int y0 = ty * from, y1 = (ty + 1) * from;
            for (int tx = 0; tx < to; tx++)
            {
                int x0 = tx * from, x1 = (tx + 1) * from;
                long sum = 0;
                for (int sy = y0 / to; sy * to < y1; sy++)
                {
                    int hy = Math.Min(y1, (sy + 1) * to) - Math.Max(y0, sy * to);
                    if (hy <= 0) { continue; }
                    for (int sx = x0 / to; sx * to < x1; sx++)
                    {
                        int hx = Math.Min(x1, (sx + 1) * to) - Math.Max(x0, sx * to);
                        if (hx > 0) { sum += (long)source[sy * from + sx] * hx * hy; }
                    }
                }
                target[ty * to + tx] = (byte)Math.Min(255, (sum + (long)from * from / 2) / ((long)from * from));
            }
        }
    }
}
