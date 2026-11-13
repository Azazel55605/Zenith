using System;

namespace Zenith.Gui.Graphics;

/// <summary>What the pointer shows, in the order of the generated images (CSS names in
/// <see cref="CursorImages.Names"/>).</summary>
internal enum CursorShape { Default, Text, Pointer, Wait, Progress, NotAllowed, Move, NsResize, EwResize, NwseResize, NeswResize, Crosshair }

/// <summary>The Meridian cursor set (Light and Dark), decoded on first use from the generated runs.
/// Pure: host-tested.</summary>
internal static partial class CursorImages
{
    public const int Variants = 2;
    private static byte[]? s_runs;
    // Lazy: static initializers of the two partial files run in no guaranteed order.
    private static uint[]?[]? s_images;

    /// <summary>The image (premultiplied ARGB, <paramref name="size"/> square) and its hotspot.</summary>
    public static uint[] Get(int variant, CursorShape shape, int size, out int hotX, out int hotY)
    {
        int index = -1;
        for (int i = 0; i < Table.Length; i += 7)
        {
            if (Table[i] == Math.Clamp(variant, 0, Variants - 1) && Table[i + 1] == (int)shape && Table[i + 2] == size) { index = i / 7; break; }
        }
        if (index < 0) { throw new ArgumentOutOfRangeException(nameof(size), "no cursor image at this size"); }
        hotX = Table[index * 7 + 5]; hotY = Table[index * 7 + 6];
        s_images ??= new uint[Table.Length / 7][];
        return s_images[index] ??= Decode(index, size);
    }

    /// <summary>The packaged size nearest to <paramref name="pixels"/> (one per display scale: 24, 30,
    /// 36, 42 and 48); a tie takes the smaller.</summary>
    public static int SizeFor(int pixels)
    {
        int best = Sizes[0];
        foreach (int size in Sizes) { if (Math.Abs(size - pixels) < Math.Abs(best - pixels)) { best = size; } }
        return best;
    }

    private static uint[] Decode(int index, int size)
    {
        s_runs ??= Convert.FromBase64String(Runs);
        int offset = Table[index * 7 + 3], length = Table[index * 7 + 4];
        var pixels = new uint[size * size]; int at = 0;
        for (int i = offset; i + 5 <= offset + length; i += 5)
        {
            uint value = (uint)(s_runs[i + 1] | s_runs[i + 2] << 8 | s_runs[i + 3] << 16 | s_runs[i + 4] << 24);
            for (int n = s_runs[i]; n > 0 && at < pixels.Length; n--) { pixels[at++] = value; }
        }
        if (at != pixels.Length) { throw new InvalidOperationException("cursor image runs do not fill the image"); }
        return pixels;
    }
}
