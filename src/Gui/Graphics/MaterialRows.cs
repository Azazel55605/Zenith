using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Zenith.Gui.Graphics;

/// <summary>The row operations behind material painting. Only SSE2 is used: the scheduler
/// saves XMM registers across task switches but nothing enables or saves wider ones. Both
/// paths produce identical pixels, which the host tests require.</summary>
internal static unsafe class MaterialRows
{
    /// <summary>Tests clear this to run the scalar path.</summary>
    internal static bool UseSse2 = Sse2.IsSupported;

    private static int Average(int a, int b) => unchecked((int)(((uint)a | (uint)b) - ((((uint)a ^ (uint)b) >> 1) & 0x7f7f7f7fU)));

    /// <summary>Mixes two rows in quarters: weight 0 is all of <paramref name="a"/>, 3 is three quarters of <paramref name="b"/>.</summary>
    public static void Mix(int[] a, int aStart, int[] b, int bStart, int[] target, int count, int weight)
    {
        if (weight == 0) { Array.Copy(a, aStart, target, 0, count); return; }
        fixed (int* pa = a, pb = b, pt = target)
        {
            int* ra = pa + aStart, rb = pb + bStart; int i = 0;
            if (UseSse2)
            {
                for (; i + 4 <= count; i += 4)
                {
                    Vector128<byte> va = Sse2.LoadVector128((byte*)(ra + i)), vb = Sse2.LoadVector128((byte*)(rb + i));
                    Vector128<byte> mid = Sse2.Average(va, vb);
                    Sse2.Store((byte*)(pt + i), weight == 2 ? mid : weight == 1 ? Sse2.Average(va, mid) : Sse2.Average(mid, vb));
                }
            }
            for (; i < count; i++)
            {
                int mid = Average(ra[i], rb[i]);
                pt[i] = weight == 2 ? mid : weight == 1 ? Average(ra[i], mid) : Average(mid, rb[i]);
            }
        }
    }

    /// <summary>Blends a tint over a row, opaque whatever the source alpha: 255 is all tint.</summary>
    public static void Tint(int[] source, int start, int[] target, int targetStart, int count, uint tint, int opacity)
    {
        int weight = opacity + (opacity >> 7), inverse = 256 - weight;
        uint red = ((tint >> 16) & 255) * (uint)weight, green = ((tint >> 8) & 255) * (uint)weight, blue = (tint & 255) * (uint)weight;
        fixed (int* ps = source, pt = target)
        {
            int* s = ps + start, t = pt + targetStart; int i = 0;
            if (UseSse2)
            {
                Vector128<short> scale = Vector128.Create((short)inverse), add = Vector128.Create((short)blue, (short)green, (short)red, 0, (short)blue, (short)green, (short)red, 0);
                Vector128<byte> zero = Vector128<byte>.Zero; Vector128<int> opaque = Vector128.Create(unchecked((int)0xff000000));
                for (; i + 4 <= count; i += 4)
                {
                    Vector128<byte> pixels = Sse2.LoadVector128((byte*)(s + i));
                    Vector128<short> low = Sse2.UnpackLow(pixels, zero).AsInt16(), high = Sse2.UnpackHigh(pixels, zero).AsInt16();
                    low = Sse2.ShiftRightLogical(Sse2.Add(Sse2.MultiplyLow(low, scale), add), 8);
                    high = Sse2.ShiftRightLogical(Sse2.Add(Sse2.MultiplyLow(high, scale), add), 8);
                    Sse2.Store(t + i, Sse2.Or(Sse2.PackUnsignedSaturate(low, high).AsInt32(), opaque));
                }
            }
            uint iv = (uint)inverse;
            for (; i < count; i++)
            {
                uint c = (uint)s[i];
                uint r = (((c >> 16) & 255) * iv + red) >> 8 & 255, g = (((c >> 8) & 255) * iv + green) >> 8 & 255, b = ((c & 255) * iv + blue) >> 8 & 255;
                t[i] = unchecked((int)(0xff000000U | (r << 16) | (g << 8) | b));
            }
        }
    }

    /// <summary>Stretches a row to four times its width with linear steps, starting <paramref name="targetStart"/> in.</summary>
    public static void Expand(int[] coarse, int coarseCount, int[] target, int targetStart, int width)
    {
        fixed (int* pc = coarse, pt = target)
        {
            int* t = pt + targetStart; int cell = 0, at = 0;
            if (UseSse2)
            {
                for (; cell + 5 <= coarseCount && at + 16 <= width; cell += 4, at += 16)
                {
                    Vector128<byte> a = Sse2.LoadVector128((byte*)(pc + cell)), b = Sse2.LoadVector128((byte*)(pc + cell + 1));
                    Vector128<byte> mid = Sse2.Average(a, b), q1 = Sse2.Average(a, mid), q3 = Sse2.Average(mid, b);
                    // Transpose so each coarse pixel's four steps land next to each other.
                    Vector128<int> a1l = Sse2.UnpackLow(a.AsInt32(), q1.AsInt32()), a1h = Sse2.UnpackHigh(a.AsInt32(), q1.AsInt32());
                    Vector128<int> m3l = Sse2.UnpackLow(mid.AsInt32(), q3.AsInt32()), m3h = Sse2.UnpackHigh(mid.AsInt32(), q3.AsInt32());
                    Sse2.Store(t + at, Sse2.UnpackLow(a1l.AsInt64(), m3l.AsInt64()).AsInt32());
                    Sse2.Store(t + at + 4, Sse2.UnpackHigh(a1l.AsInt64(), m3l.AsInt64()).AsInt32());
                    Sse2.Store(t + at + 8, Sse2.UnpackLow(a1h.AsInt64(), m3h.AsInt64()).AsInt32());
                    Sse2.Store(t + at + 12, Sse2.UnpackHigh(a1h.AsInt64(), m3h.AsInt64()).AsInt32());
                }
            }
            for (; at < width; cell++)
            {
                int a = pc[Math.Min(cell, coarseCount - 1)], b = pc[Math.Min(cell + 1, coarseCount - 1)];
                int mid = Average(a, b);
                t[at++] = a;
                if (at < width) { t[at++] = Average(a, mid); }
                if (at < width) { t[at++] = mid; }
                if (at < width) { t[at++] = Average(mid, b); }
            }
        }
    }
}
