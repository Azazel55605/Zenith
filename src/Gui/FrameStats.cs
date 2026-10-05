using System.Diagnostics;

namespace MyOS.Gui;

/// <summary>Running counters for the compositor, read by the <c>fps</c> shell command.</summary>
internal static class FrameStats
{
    public static long Frames { get; private set; }
    public static long SceneComposes { get; private set; }
    public static long LastComposeMicros { get; private set; }
    public static long LastPresentMicros { get; private set; }

    public static long Now => Stopwatch.GetTimestamp();

    public static void RecordCompose(long start) { SceneComposes++; LastComposeMicros = Micros(start); }

    public static void RecordPresent(long start) { Frames++; LastPresentMicros = Micros(start); }

    private static long Micros(long start) => (Stopwatch.GetTimestamp() - start) * 1_000_000 / Stopwatch.Frequency;
}
