using System;
using System.Drawing;
using Cosmos.Kernel.System.Graphics;

namespace MyOS.Gui.Graphics;

/// <summary>
/// An off-screen, clippable drawing surface. It is a memory-backed <see cref="Canvas"/>,
/// so the whole Canvas API (text included) works on it, plus fast span-based fills,
/// anti-aliased rounded rectangles and soft shadows written directly into the pixel buffer.
/// The whole frame is composed here, then handed to the screen in one copy.
/// </summary>
internal sealed class Surface : Canvas
{
    private Rect _clip;

    public Surface(int width, int height) : base(width, height)
    {
        Pixels = GetBuffer()!;
        Bounds = new Rect(0, 0, width, height);
        _clip = Bounds;
    }

    /// <summary>Row-major ARGB pixels, <c>Width * Height</c> long.</summary>
    public int[] Pixels { get; }

    public Rect Bounds { get; }

    /// <summary>Every drawing call, including text, is clipped to this rectangle.</summary>
    public Rect Clip
    {
        get => _clip;
        set => _clip = value.Intersect(Bounds);
    }

    public void ResetClip() => _clip = Bounds;

    // The Canvas text and shape routines all funnel through DrawPoint, so clipping here
    // clips them too.
    public override void DrawPoint(Color color, int x, int y)
    {
        if (color.A != 0 && _clip.Contains(x, y))
        {
            BlendPixel(y * Width + x, (uint)color.ToArgb(), color.A);
        }
    }

    /// <summary>Copies another surface of the same size over this one.</summary>
    public void CopyFrom(Surface source) => Array.Copy(source.Pixels, Pixels, Pixels.Length);

    public void FillRect(Rect r, uint color)
    {
        Rect c = r.Intersect(_clip);
        for (int y = c.Y; y < c.Bottom; y++)
        {
            FillSpan(y, c.X, c.Right, color, (int)(color >> 24));
        }
    }

    /// <summary>Fills a rectangle whose corners are rounded with anti-aliased quarter circles.</summary>
    public void FillRoundRect(Rect r, int radius, uint color)
    {
        radius = Math.Min(radius, Math.Min(r.W, r.H) / 2);
        int alpha = (int)(color >> 24);

        for (int row = 0; row < r.H; row++)
        {
            int y = r.Y + row;
            if (y < _clip.Y || y >= _clip.Bottom)
            {
                continue;
            }

            if (row >= radius && row < r.H - radius)
            {
                FillSpan(y, r.X, r.Right, color, alpha);
                continue;
            }

            // Distance from this row's pixel centers to the corner circles' center row.
            float dy = row < radius ? radius - (row + 0.5f) : (row + 0.5f) - (r.H - radius);
            for (int col = 0; col < radius; col++)
            {
                float dx = radius - (col + 0.5f);
                float coverage = Coverage(radius - MathF.Sqrt(dx * dx + dy * dy));
                if (coverage > 0f)
                {
                    int a = (int)(alpha * coverage);
                    PlotClipped(r.X + col, y, color, a);
                    PlotClipped(r.Right - 1 - col, y, color, a);
                }
            }

            FillSpan(y, r.X + radius, r.Right - radius, color, alpha);
        }
    }

    /// <summary>Draws a 1px anti-aliased outline just inside a rounded rectangle.</summary>
    public void StrokeRoundRect(Rect r, int radius, uint color)
    {
        radius = Math.Min(radius, Math.Min(r.W, r.H) / 2);
        int alpha = (int)(color >> 24);

        // Straight edges.
        FillRect(new Rect(r.X + radius, r.Y, r.W - 2 * radius, 1), color);
        FillRect(new Rect(r.X + radius, r.Bottom - 1, r.W - 2 * radius, 1), color);
        FillRect(new Rect(r.X, r.Y + radius, 1, r.H - 2 * radius), color);
        FillRect(new Rect(r.Right - 1, r.Y + radius, 1, r.H - 2 * radius), color);

        // Corners: coverage of the ring between radius-1 and radius.
        for (int row = 0; row < radius; row++)
        {
            float dy = radius - (row + 0.5f);
            for (int col = 0; col < radius; col++)
            {
                float dx = radius - (col + 0.5f);
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float coverage = Coverage(radius - d) - Coverage(radius - 1 - d);
                if (coverage <= 0f)
                {
                    continue;
                }

                int a = (int)(alpha * coverage);
                PlotClipped(r.X + col, r.Y + row, color, a);
                PlotClipped(r.Right - 1 - col, r.Y + row, color, a);
                PlotClipped(r.X + col, r.Bottom - 1 - row, color, a);
                PlotClipped(r.Right - 1 - col, r.Bottom - 1 - row, color, a);
            }
        }
    }

    /// <summary>
    /// Draws a soft drop shadow around a rounded rectangle. Only the ring outside the
    /// rectangle's flat interior is touched, since the caller paints over the rest.
    /// </summary>
    public void DrawShadow(Rect r, int radius, int size, int maxAlpha, int offsetY)
    {
        Rect s = r.Offset(0, offsetY);
        Rect area = s.Inflate(size).Intersect(_clip);
        Rect interior = s.Inflate(-radius);
        float halfW = s.W / 2f, halfH = s.H / 2f;
        float cx = s.X + halfW, cy = s.Y + halfH;

        for (int y = area.Y; y < area.Bottom; y++)
        {
            bool rowInInterior = y >= interior.Y && y < interior.Bottom;
            for (int x = area.X; x < area.Right; x++)
            {
                if (rowInInterior && x >= interior.X && x < interior.Right)
                {
                    x = interior.Right - 1;
                    continue;
                }

                // Signed distance from the pixel center to the rounded rectangle.
                float qx = MathF.Abs(x + 0.5f - cx) - halfW + radius;
                float qy = MathF.Abs(y + 0.5f - cy) - halfH + radius;
                float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
                float distance = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - radius;

                float t = 1f - distance / size;
                if (t <= 0f)
                {
                    continue;
                }

                int a = (int)(maxAlpha * MathF.Min(t * t, 1f));
                if (a > 0)
                {
                    BlendPixel(y * Width + x, 0xFF000000, a);
                }
            }
        }
    }

    /// <summary>Draws a single line of text with its top-left at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void DrawText(string text, FontFace font, int size, uint color, int x, int y)
    {
        int alpha = (int)(color >> 24);
        for (int i = 0; i < text.Length; i++)
        {
            Glyph glyph = font.GetGlyph(text[i], size);
            if (glyph.Width > 0)
            {
                DrawGlyph(glyph, color, alpha, x + glyph.OffsetX, y + glyph.OffsetY);
            }

            x += glyph.Advance;
        }
    }

    /// <summary>Draws text vertically centered in <paramref name="box"/>, starting at its left edge.</summary>
    public void DrawTextCentered(string text, FontFace font, int size, uint color, Rect box, bool centerHorizontally)
    {
        int width = font.MeasureString(text, size);
        int x = centerHorizontally ? box.X + (box.W - width) / 2 : box.X;
        int y = box.Y + (box.H - font.GetLineHeight(size)) / 2;
        DrawText(text, font, size, color, x, y);
    }

    /// <summary>Copies a rectangle of pixels from another surface of the same size, at the same position.</summary>
    public void CopyRect(Surface source, Rect r)
    {
        Rect c = r.Intersect(Bounds);
        for (int y = c.Y; y < c.Bottom; y++)
        {
            Array.Copy(source.Pixels, y * Width + c.X, Pixels, y * Width + c.X, c.W);
        }
    }

    private void DrawGlyph(Glyph glyph, uint color, int alpha, int gx, int gy)
    {
        Rect c = new Rect(gx, gy, glyph.Width, glyph.Height).Intersect(_clip);
        byte[] coverage = glyph.Coverage;
        for (int y = c.Y; y < c.Bottom; y++)
        {
            int src = (y - gy) * glyph.Width - gx;
            int row = y * Width;
            for (int x = c.X; x < c.Right; x++)
            {
                int a = coverage[src + x];
                if (a != 0)
                {
                    BlendPixel(row + x, color, alpha == 255 ? a : a * alpha / 255);
                }
            }
        }
    }

    private void FillSpan(int y, int x0, int x1, uint color, int alpha)
    {
        x0 = Math.Max(x0, _clip.X);
        x1 = Math.Min(x1, _clip.Right);
        if (x1 <= x0 || alpha == 0)
        {
            return;
        }

        int start = y * Width;
        if (alpha == 255)
        {
            Pixels.AsSpan(start + x0, x1 - x0).Fill((int)color);
            return;
        }

        for (int i = start + x0; i < start + x1; i++)
        {
            BlendPixel(i, color, alpha);
        }
    }

    private void PlotClipped(int x, int y, uint color, int alpha)
    {
        if (alpha > 0 && _clip.Contains(x, y))
        {
            BlendPixel(y * Width + x, color, alpha);
        }
    }

    private void BlendPixel(int index, uint color, int alpha)
    {
        if (alpha >= 255)
        {
            Pixels[index] = (int)(color | 0xFF000000);
            return;
        }

        int dst = Pixels[index];
        int inv = 255 - alpha;
        int r = ((int)((color >> 16) & 0xFF) * alpha + ((dst >> 16) & 0xFF) * inv) / 255;
        int g = ((int)((color >> 8) & 0xFF) * alpha + ((dst >> 8) & 0xFF) * inv) / 255;
        int b = ((int)(color & 0xFF) * alpha + (dst & 0xFF) * inv) / 255;
        Pixels[index] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    private static float Coverage(float signedDistance) => Math.Clamp(signedDistance + 0.5f, 0f, 1f);
}
