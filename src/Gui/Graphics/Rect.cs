using System;

namespace Zenith.Gui.Graphics;

/// <summary>An integer rectangle in screen pixels. <see cref="Right"/> and <see cref="Bottom"/> are exclusive.</summary>
internal readonly struct Rect
{
    public readonly int X;
    public readonly int Y;
    public readonly int W;
    public readonly int H;

    public Rect(int x, int y, int w, int h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public int Right => X + W;
    public int Bottom => Y + H;
    public bool IsEmpty => W <= 0 || H <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, W, H);

    public Rect Inflate(int d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    /// <summary>The smallest rectangle covering both; an empty side is ignored.</summary>
    public Rect Union(Rect other)
    {
        if (IsEmpty) { return other; }
        if (other.IsEmpty) { return this; }
        int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public Rect Intersect(Rect other)
    {
        int x0 = Math.Max(X, other.X);
        int y0 = Math.Max(Y, other.Y);
        int x1 = Math.Min(Right, other.Right);
        int y1 = Math.Min(Bottom, other.Bottom);
        return x1 <= x0 || y1 <= y0 ? new Rect(x0, y0, 0, 0) : new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
