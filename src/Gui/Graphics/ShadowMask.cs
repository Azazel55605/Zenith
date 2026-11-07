using System;
using System.Collections.Generic;

namespace Zenith.Gui.Graphics;

/// <summary>
/// A window shadow's alpha depends only on how far a pixel lies past the rounded edges (qx, qy),
/// which step in whole pixels plus a fraction fixed per rectangle. The table holds it once per
/// style instead of a square root per pixel per frame. Offsets at or below zero all give full
/// alpha and offsets past radius+size give none, so index -1 and the last index cover them exactly.
/// </summary>
internal readonly struct ShadowMask
{
    private static readonly Dictionary<long, byte[]> s_tables = new();
    private readonly byte[] _table;
    private readonly int _n, _radius;
    private readonly float _cx, _cy, _halfW, _halfH;

    /// <summary>The original per-pixel formula; the table must reproduce it exactly.</summary>
    public static int Exact(float qx, float qy, int radius, int size, int maxAlpha)
    {
        float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
        float distance = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
        float t = 1f - distance / size;
        return t <= 0f ? 0 : (int)(maxAlpha * MathF.Min(t * t, 1f));
    }

    private ShadowMask(byte[] table, int n, int radius, float cx, float cy, float halfW, float halfH)
    { _table = table; _n = n; _radius = radius; _cx = cx; _cy = cy; _halfW = halfW; _halfH = halfH; }

    public static ShadowMask For(Rect s, int radius, int size, int maxAlpha)
    {
        float halfW = s.W / 2f, halfH = s.H / 2f, cx = s.X + halfW, cy = s.Y + halfH;
        int n = radius + size + 2;
        float firstX = MathF.Abs(s.X + 0.5f - cx) - halfW + radius, firstY = MathF.Abs(s.Y + 0.5f - cy) - halfH + radius;
        float fx = firstX - MathF.Floor(firstX), fy = firstY - MathF.Floor(firstY);
        long key = radius | (long)size << 8 | (long)maxAlpha << 16 | (fx != 0 ? 1L : 0) << 24 | (fy != 0 ? 1L : 0) << 25;
        byte[]? table;
        lock (s_tables)
        {
            if (!s_tables.TryGetValue(key, out table))
            {
                table = new byte[n * n];
                for (int iy = -1; iy < n - 1; iy++)
                    for (int ix = -1; ix < n - 1; ix++)
                        table[(iy + 1) * n + ix + 1] = (byte)Exact(ix + fx, iy + fy, radius, size, maxAlpha);
                if (s_tables.Count < 64) { s_tables[key] = table; }
            }
        }
        return new ShadowMask(table, n, radius, cx, cy, halfW, halfH);
    }

    public int Row(int y) => Math.Clamp((int)MathF.Floor(MathF.Abs(y + 0.5f - _cy) - _halfH + _radius), -1, _n - 2) + 1;
    public int Alpha(int x, int row) => _table[row * _n + Math.Clamp((int)MathF.Floor(MathF.Abs(x + 0.5f - _cx) - _halfW + _radius), -1, _n - 2) + 1];
}
