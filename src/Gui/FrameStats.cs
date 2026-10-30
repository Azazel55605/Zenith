using System;
using System.Diagnostics;

namespace Zenith.Gui;

/// <summary>Running counters for the compositor, read by the <c>fps</c> shell command.</summary>
internal static class FrameStats
{
    public static long Frames { get; private set; }
    public static long SceneComposes { get; private set; }
    public static long LastComposeMicros { get; private set; }
    public static long LastPresentMicros { get; private set; }

    // Whole scene frames since the last reset: the budget animations have to fit in.
    public static long SceneFrames { get; private set; }
    public static long TotalFrameMicros { get; private set; }
    public static long MaxFrameMicros { get; private set; }
    public static long MaxComposeMicros { get; private set; }
    public static long MaxPresentMicros { get; private set; }
    public static long FramesOver16Ms { get; private set; }
    public static long FramesOver33Ms { get; private set; }
    public static long AverageFrameMicros => SceneFrames == 0 ? 0 : TotalFrameMicros / SceneFrames;

    public static long Now => Stopwatch.GetTimestamp();

    public static void RecordCompose(long start) { SceneComposes++; LastComposeMicros = Micros(start); MaxComposeMicros = Math.Max(MaxComposeMicros, LastComposeMicros); }

    public static void RecordPresent(long start) { Frames++; LastPresentMicros = Micros(start); MaxPresentMicros = Math.Max(MaxPresentMicros, LastPresentMicros); }

    public static void RecordSceneFrame(long start) => AddSceneFrame(Micros(start));

    public static void AddSceneFrame(long micros)
    {
        SceneFrames++; TotalFrameMicros += micros; MaxFrameMicros = Math.Max(MaxFrameMicros, micros);
        if (micros > 16_667) { FramesOver16Ms++; }
        if (micros > 33_333) { FramesOver33Ms++; }
    }

    /// <summary>Starts a new measurement, so one action can be timed on its own.</summary>
    public static void ResetWindow()
    {
        SceneFrames = TotalFrameMicros = MaxFrameMicros = MaxComposeMicros = MaxPresentMicros = FramesOver16Ms = FramesOver33Ms = 0;
    }

    private static long Micros(long start) => (Stopwatch.GetTimestamp() - start) * 1_000_000 / Stopwatch.Frequency;
}
