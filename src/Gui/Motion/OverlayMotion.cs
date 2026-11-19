using System.Diagnostics;

namespace Zenith.Gui.Motion;

/// <summary>Popups, panels and dialogs rise into place instead of appearing at once (docs/design/motion-additions-concept.svg,
/// kernel 0.0.71): a short ease-out of a few pixels, scaled by the animation setting; Off, or any input, lands them at
/// once. The desktop keeps drawing while one is moving.</summary>
internal static class OverlayMotion
{
    public const int Centers = 0, Prism = 1, Overflow = 2, Dialog = 3, Count = 4;
    private static readonly long[] s_start = new long[Count];
    private static readonly int[] s_distance = { 8, 12, 8, 10 }, s_micros = { 140_000, 140_000, 120_000, 150_000 };
    private static long Now => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;
    /// <summary>The animation setting in percent (0 = Off), set by the desktop.</summary>
    public static System.Func<int> AnimationPercent = () => 100;
    private static int Percent => AnimationPercent();

    public static void Start(int which)
    {
        if (Percent == 0) { s_start[which] = 0; return; }
        s_start[which] = Now;
    }
    /// <summary>Lands every overlay at once (a key, click or wheel).</summary>
    public static void Finish() { for (int i = 0; i < Count; i++) { s_start[i] = 0; } }
    /// <summary>How far below its place the overlay is drawn now (0 when it has arrived); positive moves it down.</summary>
    public static int Offset(int which)
    {
        if (s_start[which] == 0) { return 0; }
        long duration = (long)s_micros[which] * Percent / 100, elapsed = Now - s_start[which];
        if (duration <= 0 || elapsed >= duration) { s_start[which] = 0; return 0; }
        int eased = MotionCurve.EaseOut(MotionCurve.Linear(elapsed, duration));
        return s_distance[which] - s_distance[which] * eased / MotionCurve.One;
    }
    public static bool Active
    {
        get { for (int i = 0; i < Count; i++) { if (s_start[i] != 0) { return true; } } return false; }
    }
}
