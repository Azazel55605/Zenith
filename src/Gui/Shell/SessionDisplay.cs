using Cosmos.Kernel.System.Graphics;
using Zenith.Gui.Motion;
using MemoryInfo = Cosmos.Kernel.System.Diagnostics.MemoryInfo;

namespace Zenith.Gui.Shell;

/// <summary>The screen, which outlives sessions, and the crossfade between two of them. The copy of
/// the old session's last frame exists only until the next session draws, and is wiped after use.</summary>
internal static class SessionDisplay
{
    private const long FadeMicros = 220_000, WelcomeFadeMicros = 420_000, SlowFrameMicros = 33_333;
    private const ulong ReservePages = 4096;
    private static Canvas? s_screen;
    private static int[]? s_fade;
    public static Canvas Screen => s_screen ??= Canvas.GetFullScreen();

    public static bool FadePending => s_fade is not null;

    /// <summary>Remembers the last frame of a stopping session; skipped when fades are off or memory is short.</summary>
    public static void KeepFrame(int[] pixels)
    {
        DropFrame();
        if (Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent == 0) { return; }
        if (MemoryInfo.FreePages < (ulong)(pixels.Length * sizeof(int) * 2 / 4096) + ReservePages) { return; }
        s_fade = (int[])pixels.Clone();
    }

    public static void DropFrame()
    {
        if (s_fade is not null) { System.Array.Clear(s_fade); }
        s_fade = null;
    }

    /// <summary>Fades from the kept frame to this one, always ending on it. Slow frames cut it short. Signing in only
    /// removes a card from the same wallpaper, so <paramref name="welcome"/> takes longer and eases both ways to be seen.</summary>
    public static void FadeTo(int[] frame, int width, int height, bool welcome = false)
    {
        int[]? from = s_fade;
        s_fade = null;
        Canvas screen = Screen;
        int frames = 0, slow = 0;
        long worst = 0, elapsed = 0;
        string end = "Finished";
        if (from is not null && from.Length == frame.Length)
        {
            int[] mix = new int[frame.Length];
            long duration = (welcome ? WelcomeFadeMicros : FadeMicros) * Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent / 100;
            long start = Now();
            while ((elapsed = Now() - start) < duration)
            {
                long frameStart = Now();
                int linear = MotionCurve.Linear(elapsed, duration);
                PixelBlend.Mix(from, frame, mix, (welcome ? MotionCurve.EaseInOut(linear) : MotionCurve.EaseOut(linear)) * 255 / MotionCurve.One);
                screen.DrawArray(mix, 0, 0, width, height);
                screen.Display();
                long took = Now() - frameStart;
                frames++;
                worst = System.Math.Max(worst, took);
                slow = took > SlowFrameMicros ? slow + 1 : 0;
                if (slow >= 2) { end = "OverBudget"; break; }
            }
            System.Array.Clear(mix);
        }
        if (from is not null) { System.Array.Clear(from); }
        screen.DrawArray(frame, 0, 0, width, height);
        screen.Display();
        Zenith.Core.Log.Write("motion", "SessionFade frames=" + frames + " elapsed=" + elapsed + " worst=" + worst + " ended=" + end);
    }

    private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp() / System.Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 1_000_000);
}
