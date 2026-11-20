using System;
using Zenith.Gui.Graphics;
using Zenith.Gui.Motion;

namespace Zenith.Gui.Shell;

/// <summary>The session splash styles (docs/design/startup-concept.svg, kernel 0.0.72): how signing in turns into the
/// desktop. The desktop is already running underneath; every style ends on its real frame, a slow machine cuts it
/// short, and Animations › Off skips it. Crossfade is the original; Rise, Iris and Greeting are new.</summary>
internal static class SplashStyles
{
    public const int Crossfade = 0, Rise = 1, Iris = 2, Greeting = 3;
    public static int Current => (int)Math.Min(3u, Zenith.Core.Configuration.SystemConfiguration.Settings?.Style(13) ?? 0);
    public static long Micros(int style) => style switch { Rise => 480_000, Iris => 520_000, Greeting => 900_000, _ => 420_000 };

    /// <summary>One frame of <paramref name="style"/> at <paramref name="progress"/> (0..MotionCurve.One) into <paramref name="output"/>.
    /// <paramref name="greeting"/> is the greeting card for that style (drawn once).</summary>
    public static void Frame(int style, int[] from, int[] to, int[]? greeting, int[] output, int width, int height, int progress)
    {
        switch (style)
        {
            case Rise: RiseFrame(from, to, output, width, height, progress); break;
            case Iris: IrisFrame(from, to, output, width, height, progress); break;
            case Greeting when greeting is not null:
            {
                // Hold the greeting from 30 % to 60 %, then let the desktop through.
                int one = MotionCurve.One;
                if (progress < one * 3 / 10) { PixelBlend.Mix(from, greeting, output, MotionCurve.EaseOut(progress * 10 / 3) * 255 / one); }
                else if (progress < one * 6 / 10) { Array.Copy(greeting, output, output.Length); }
                else { PixelBlend.Mix(greeting, to, output, MotionCurve.EaseInOut((progress - one * 6 / 10) * 10 / 4) * 255 / one); }
                break;
            }
            default: PixelBlend.Mix(from, to, output, MotionCurve.EaseInOut(progress) * 255 / MotionCurve.One); break;
        }
    }

    /// <summary>The desktop grows from 94 % as the login fades; it is fully there from two thirds on.</summary>
    private static void RiseFrame(int[] from, int[] to, int[] output, int width, int height, int progress)
    {
        int one = MotionCurve.One, eased = MotionCurve.EaseOut(progress);
        int scale = 940 + 60 * eased / one;                  // per mille
        int alpha = Math.Min(255, eased * 255 * 3 / 2 / one);
        int cx = width / 2, cy = height / 2;
        uint a = (uint)(alpha + (alpha >> 7)), inverse = 256 - a;
        for (int y = 0; y < height; y++)
        {
            int sy = cy + (y - cy) * 1000 / scale;
            for (int x = 0; x < width; x++)
            {
                int sx = cx + (x - cx) * 1000 / scale, at = y * width + x;
                uint d = (uint)from[at];
                // Outside the shrunken desktop the login shows on its own.
                uint s = sx >= 0 && sy >= 0 && sx < width && sy < height ? (uint)to[sy * width + sx] : d;
                uint rb = (((s & 0xff00ff) * a + (d & 0xff00ff) * inverse) >> 8) & 0xff00ff;
                uint g = (((s & 0xff00) * a + (d & 0xff00) * inverse) >> 8) & 0xff00;
                output[at] = (int)(0xff000000 | rb | g);
            }
        }
    }

    /// <summary>A circle opens from where the account picture was until it covers the screen, with a soft edge.</summary>
    private static void IrisFrame(int[] from, int[] to, int[] output, int width, int height, int progress)
    {
        int cx = width / 2, cy = height * 39 / 100;
        long far = (long)Math.Max(cx, width - cx) * Math.Max(cx, width - cx) + (long)Math.Max(cy, height - cy) * Math.Max(cy, height - cy);
        double maxRadius = Math.Sqrt(far) + 24;
        double radius = maxRadius * MotionCurve.EaseInOut(progress) / MotionCurve.One;
        const double edge = 24;
        for (int y = 0; y < height; y++)
        {
            long dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x; long dx = x - cx;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= radius - edge) { output[at] = to[at]; continue; }
                if (d >= radius) { output[at] = from[at]; continue; }
                uint a = (uint)((radius - d) / edge * 256), inverse = 256 - a, s = (uint)to[at], f = (uint)from[at];
                uint rb = (((s & 0xff00ff) * a + (f & 0xff00ff) * inverse) >> 8) & 0xff00ff;
                uint g = (((s & 0xff00) * a + (f & 0xff00) * inverse) >> 8) & 0xff00;
                output[at] = (int)(0xff000000 | rb | g);
            }
        }
    }

    /// <summary>The greeting card: the desktop's wallpaper, dimmed, with "Good evening, NAME" and the time.</summary>
    public static int[] GreetingCard(int[] to, int width, int height, string name)
    {
        var surface = new Surface(width, height);
        Array.Copy(to, surface.Pixels, Math.Min(to.Length, surface.Pixels.Length));
        surface.FillRect(new Rect(0, 0, width, height), 0x99000000U);
        DateTime now = Zenith.Core.Time.SystemClock.Now;
        string part = now.Hour < 5 ? "Good night" : now.Hour < 12 ? "Good morning" : now.Hour < 18 ? "Good afternoon" : "Good evening";
        string time = now.Hour.ToString("00") + ":" + now.Minute.ToString("00");
        surface.DrawTextCentered(part + ", " + name, Fonts.SemiBold, 28, 0xFFF1F2F4U, new Rect(0, height / 2 - 44, width, 40), true);
        surface.DrawTextCentered(time, Fonts.Regular, 18, 0xFFB4B7C0U, new Rect(0, height / 2 + 8, width, 28), true);
        return surface.Pixels;
    }
}
