using System;

namespace Zenith.Core.Multiprocessing;

/// <summary>Deterministic radius-four, separable material filter. The scalar
/// fallback and CPU workers round at the same two pass boundaries.</summary>
internal static class BoxBlur
{
    internal const int TileWidth = 256, TileHeight = 64;

    /// <summary>A complete tile, including the region's clamped halo. Scratch
    /// belongs to the caller; it is never shared with an AP.</summary>
    internal static void Tile(int[] source, int offset, int width, int height,
        int x, int y, int w, int h, int[] scratch, int[] result)
    {
        for (int dy = 0; dy < h + 8; dy++)
        {
            int row = offset + Math.Clamp(y + dy - 4, 0, height - 1) * width;
            int red = 0, green = 0, blue = 0;
            for (int k = -4; k <= 4; k++)
            {
                int c = source[row + Math.Clamp(x + k, 0, width - 1)];
                red += (c >> 16) & 255; green += (c >> 8) & 255; blue += c & 255;
            }
            for (int dx = 0; dx < w; dx++)
            {
                scratch[dy * w + dx] = Color(red, green, blue);
                int a = source[row + Math.Clamp(x + dx - 4, 0, width - 1)];
                int b = source[row + Math.Clamp(x + dx + 5, 0, width - 1)];
                red += ((b >> 16) & 255) - ((a >> 16) & 255);
                green += ((b >> 8) & 255) - ((a >> 8) & 255); blue += (b & 255) - (a & 255);
            }
        }
        for (int dx = 0; dx < w; dx++)
        {
            int red = 0, green = 0, blue = 0;
            for (int k = 0; k < 9; k++)
            {
                int c = scratch[k * w + dx];
                red += (c >> 16) & 255; green += (c >> 8) & 255; blue += c & 255;
            }
            for (int dy = 0; dy < h; dy++)
            {
                result[dy * w + dx] = Color(red, green, blue);
                if (dy + 1 == h) { continue; }
                int a = scratch[dy * w + dx], b = scratch[(dy + 9) * w + dx];
                red += ((b >> 16) & 255) - ((a >> 16) & 255);
                green += ((b >> 8) & 255) - ((a >> 8) & 255); blue += (b & 255) - (a & 255);
            }
        }
    }

    private static int Color(int red, int green, int blue)
        => unchecked((int)0xff000000) | ((red + 4) / 9 << 16) | ((green + 4) / 9 << 8) | (blue + 4) / 9;

    internal static void Pass(int[] source, int[] target, int offset, int width, int height, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
        {
            int start = offset + (horizontal ? line * width : line), step = horizontal ? 1 : width;
            int red = 0, green = 0, blue = 0;
            for (int k = -4; k <= 4; k++)
            {
                int color = source[start + Math.Clamp(k, 0, length - 1) * step];
                red += (color >> 16) & 255; green += (color >> 8) & 255; blue += color & 255;
            }
            for (int p = 0; p < length; p++)
            {
                target[start + p * step] = unchecked((int)0xff000000) | ((red + 4) / 9 << 16) | ((green + 4) / 9 << 8) | (blue + 4) / 9;
                int remove = source[start + Math.Clamp(p - 4, 0, length - 1) * step];
                int add = source[start + Math.Clamp(p + 5, 0, length - 1) * step];
                red += ((add >> 16) & 255) - ((remove >> 16) & 255);
                green += ((add >> 8) & 255) - ((remove >> 8) & 255); blue += (add & 255) - (remove & 255);
            }
        }
    }
}
