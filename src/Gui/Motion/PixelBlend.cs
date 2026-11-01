using System;

namespace Zenith.Gui.Motion;

/// <summary>Mixes finished frames, for crossfades.</summary>
internal static class PixelBlend
{
    /// <summary>Writes <paramref name="to"/> over <paramref name="from"/> at <paramref name="alpha"/> (0-255) into <paramref name="output"/>.</summary>
    public static void Mix(int[] from, int[] to, int[] output, int alpha)
    {
        // On a scale of 0-256, so that both ends give back the exact frame.
        uint a = (uint)(alpha + (alpha >> 7)), inverse = 256 - a;
        for (int i = 0; i < output.Length; i++)
        {
            uint s = (uint)to[i], d = (uint)from[i];
            uint rb = (((s & 0xff00ff) * a + (d & 0xff00ff) * inverse) >> 8) & 0xff00ff;
            uint g = (((s & 0xff00) * a + (d & 0xff00) * inverse) >> 8) & 0xff00;
            output[i] = (int)(0xff000000 | rb | g);
        }
    }

    /// <summary>Mixes one rectangle of two same-sized surfaces into <paramref name="output"/>, all four channels, so it
    /// also holds for premultiplied foregrounds. <paramref name="to"/> sits <paramref name="drop"/> rows lower, repeating the
    /// rectangle's top row above itself. <paramref name="alpha"/> runs 0-256, where both ends are exact.</summary>
    public static void MixRegion(int[] from, int[] to, int[] output, int stride, int x, int y, int width, int height, int drop, int alpha)
    {
        uint a = (uint)alpha, inverse = 256 - a;
        for (int row = y; row < y + height; row++)
        {
            int source = Math.Max(y, row - drop) * stride, target = row * stride;
            for (int column = x; column < x + width; column++)
            {
                uint s = (uint)to[source + column], d = (uint)from[target + column];
                uint rb = (((s & 0xff00ff) * a + (d & 0xff00ff) * inverse) >> 8) & 0xff00ff;
                uint ag = ((s >> 8) & 0xff00ff) * a + ((d >> 8) & 0xff00ff) * inverse;
                output[target + column] = (int)(rb | (ag & 0xff00ff00));
            }
        }
    }
}
