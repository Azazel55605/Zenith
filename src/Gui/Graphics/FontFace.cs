using System;
using System.Collections.Generic;
using System.Drawing;
using Cosmos.Kernel.System.Graphics.Fonts;

namespace MyOS.Gui.Graphics;

/// <summary>An anti-aliased glyph bitmap, positioned relative to the top-left of its text line.</summary>
internal sealed class Glyph
{
    public Glyph(byte[] coverage, int offsetX, int offsetY, int width, int height, int advance)
    {
        Coverage = coverage;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Width = width;
        Height = height;
        Advance = advance;
    }

    public byte[] Coverage { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }
    public int Width { get; }
    public int Height { get; }
    public int Advance { get; }
}

/// <summary>
/// A TrueType font with a per-(size, character) glyph cache. Cosmos keeps its glyph bitmaps
/// internal and draws them one virtual <c>DrawPoint</c> call per pixel; here each glyph is
/// rasterized once through the public API (white on black, so the red channel is the
/// coverage), and from then on text is a tight blend loop in <see cref="Surface.DrawText"/>.
/// </summary>
internal sealed class FontFace
{
    private readonly TrueTypeFont _font;
    private readonly Dictionary<int, Glyph> _glyphs = new();
    private readonly Dictionary<int, int> _lineHeights = new();
    private Surface? _scratch;

    public FontFace(byte[] data)
    {
        _font = new TrueTypeFont(data);
    }

    public int GetLineHeight(int size)
    {
        if (!_lineHeights.TryGetValue(size, out int height))
        {
            height = _font.GetLineHeight(size);
            _lineHeights[size] = height;
        }

        return height;
    }

    public int MeasureString(string text, int size)
    {
        int width = 0;
        for (int i = 0; i < text.Length; i++)
        {
            width += GetGlyph(text[i], size).Advance;
        }

        return width;
    }

    public Glyph GetGlyph(char c, int size)
    {
        int key = (size << 16) | c;
        if (!_glyphs.TryGetValue(key, out Glyph? glyph))
        {
            glyph = Rasterize(c, size);
            _glyphs[key] = glyph;
        }

        return glyph;
    }

    private Glyph Rasterize(char c, int size)
    {
        int advance = _font.MeasureString(c.ToString(), size);
        int pad = size;
        int boxW = Math.Max(advance, size) + 2 * pad;
        int boxH = GetLineHeight(size) + 2 * pad;

        if (_scratch is null || _scratch.Width < boxW || _scratch.Height < boxH)
        {
            _scratch = new Surface(Math.Max(boxW, 128), Math.Max(boxH, 128));
        }

        int[] pixels = _scratch.Pixels;
        int stride = _scratch.Width;
        Array.Fill(pixels, unchecked((int)0xFF000000));
        _scratch.DrawString(c.ToString(), _font, size, Color.White, pad, pad);

        // Bounding box of the inked pixels.
        int minX = boxW, minY = boxH, maxX = -1, maxY = -1;
        for (int y = 0; y < boxH; y++)
        {
            for (int x = 0; x < boxW; x++)
            {
                if (((pixels[y * stride + x] >> 16) & 0xFF) != 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < 0)
        {
            return new Glyph(Array.Empty<byte>(), 0, 0, 0, 0, advance);   // whitespace
        }

        int w = maxX - minX + 1, h = maxY - minY + 1;
        byte[] coverage = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                coverage[y * w + x] = (byte)((pixels[(minY + y) * stride + minX + x] >> 16) & 0xFF);
            }
        }

        return new Glyph(coverage, minX - pad, minY - pad, w, h, advance);
    }
}
