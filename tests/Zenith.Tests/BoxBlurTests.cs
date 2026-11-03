using System;
using System.Linq;
using Zenith.Core.Multiprocessing;

namespace Zenith.Tests;

public class BoxBlurTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 137)]
    [InlineData(269, 1)]
    [InlineData(257, 65)]
    [InlineData(513, 139)]
    public void Tiled_filter_matches_independent_two_pass_oracle_and_keeps_guards(int width, int height)
    {
        const int offset = 7;
        int[] source = new int[width * height + 14], scratch = new int[source.Length], expected = new int[source.Length], actual = new int[source.Length];
        var random = new Random(721); for (int i = 0; i < source.Length; i++) source[i] = random.Next(int.MinValue, int.MaxValue);
        Array.Fill(actual, 0x12345678);
        // Deliberately slow, direct nine-sample oracle; no sliding sums/tiles.
        for (int pass = 0; pass < 2; pass++)
        {
            int[] input = pass == 0 ? source : scratch, output = pass == 0 ? scratch : expected;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int k = -4; k <= 4; k++)
                {
                    int c = input[offset + Math.Clamp(y + (pass == 1 ? k : 0), 0, height - 1) * width + Math.Clamp(x + (pass == 0 ? k : 0), 0, width - 1)];
                    r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                }
                output[offset + y * width + x] = unchecked((int)0xff000000) | ((r + 4) / 9 << 16) | ((g + 4) / 9 << 8) | (b + 4) / 9;
            }
        }
        int[] rows = new int[BoxBlur.TileWidth * (BoxBlur.TileHeight + 8)], tile = new int[BoxBlur.TileWidth * BoxBlur.TileHeight];
        for (int y = 0; y < height; y += BoxBlur.TileHeight) for (int x = 0; x < width; x += BoxBlur.TileWidth)
        {
            int w = Math.Min(width - x, BoxBlur.TileWidth), h = Math.Min(height - y, BoxBlur.TileHeight);
            BoxBlur.Tile(source, offset, width, height, x, y, w, h, rows, tile);
            for (int row = 0; row < h; row++) Array.Copy(tile, row * w, actual, offset + (y + row) * width + x, w);
        }
        Assert.Equal(expected.AsSpan(offset, width * height).ToArray(), actual.AsSpan(offset, width * height).ToArray());
        Assert.All(actual.Take(offset), c => Assert.Equal(0x12345678, c));
        Assert.All(actual.Skip(offset + width * height), c => Assert.Equal(0x12345678, c));
        BoxBlur.Pass(source, scratch, offset, width, height, true); BoxBlur.Pass(scratch, actual, offset, width, height, false);
        Assert.Equal(expected.AsSpan(offset, width * height).ToArray(), actual.AsSpan(offset, width * height).ToArray());
    }
}
