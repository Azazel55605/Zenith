namespace Zenith.Gui.Motion;

/// <summary>Fixed-point animation progress: 0 at the start, <see cref="One"/> at the end.</summary>
internal static class MotionCurve
{
    public const int One = 1024;

    public static int Linear(long elapsed, long duration)
    {
        if (duration <= 0 || elapsed >= duration) { return One; }
        return elapsed <= 0 ? 0 : (int)(elapsed * One / duration);
    }

    /// <summary>Cubic ease-out: moves quickly, then settles.</summary>
    public static int EaseOut(int linear)
    {
        long rest = One - linear;
        return One - (int)(rest * rest * rest / (One * One));
    }

    public static int Lerp(int from, int to, int progress) => from + (int)((long)(to - from) * progress / One);
}
