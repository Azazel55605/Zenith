namespace Zenith.Gui.Motion;

/// <summary>Mixes two whole frames, for crossfades.</summary>
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
}
