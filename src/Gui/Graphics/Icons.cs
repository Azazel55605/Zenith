using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Packages;

namespace Zenith.Gui.Graphics;

/// <summary>Package-installed system icons for trusted windows; a missing icon draws nothing.</summary>
internal static class Icons
{
    private const int MaximumCached = 64;
    private static readonly Dictionary<string, byte[]?> s_files = new();
    private static byte[]? Load(string name)
    {
        if (s_files.TryGetValue(name, out byte[]? known)) { return known; }
        byte[]? bytes = null;
        if (s_files.Count < MaximumCached && IconFile.ValidName(name))
        {
            try
            {
                string path = "/usr/share/zenith/icons/" + name + ".zic";
                if (File.Exists(path) && new FileInfo(path).Length <= IconFile.MaximumBytes) { bytes = File.ReadAllBytes(path); }
            }
            catch (Exception) { bytes = null; }
            s_files[name] = bytes;
        }
        return bytes;
    }
    public static bool Draw(Surface surface, string name, int x, int y, int size, uint color)
    {
        byte[]? file = Load(name);
        if (size is < 1 or > 64 || file is null || !IconFile.TryFind(file, size, out var runs)) { return false; }
        Span<byte> coverage = stackalloc byte[size * size];
        if (!IconFile.TryDecode(runs, size, coverage)) { return false; }
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
}
