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
    // Observatory's view: never reset by fps, and the worst frame since Observatory last asked.
    public static long LifetimeSceneFrames { get; private set; }
    public static long LifetimeFrameMicros { get; private set; }
    private static long s_monitorWorst;
    public static (long Presented, long Scenes, long Micros, long Worst) TakeMonitorFrames()
    { long worst = s_monitorWorst; s_monitorWorst = 0; return (Frames, LifetimeSceneFrames, LifetimeFrameMicros, worst); }

    public static long Now => Stopwatch.GetTimestamp();

    // Where compose time goes, accumulated between TakePhases calls. Sample, blur and range split the
    // backdrop's material captures: averaging the scene, filtering, and the luminance range.
    public static long BackdropTicks, ChromeTicks, ContentTicks, ShellTicks, SampleTicks, BlurTicks, RangeTicks;
    // Scene composes split by pass (lower windows, top window), for `fps phases`.
    public static long LowerTicks, TopTicks, ComposeCount, FullComposes;
    public static string TakePhases(int frames)
    {
        long per = Math.Max(1, frames) * Math.Max(1, Stopwatch.Frequency / 1_000_000);
        string text = "backdrop=" + BackdropTicks / per + " chrome=" + ChromeTicks / per + " content=" + ContentTicks / per + " shell=" + ShellTicks / per
            + " sample=" + SampleTicks / per + " blur=" + BlurTicks / per + " range=" + RangeTicks / per;
        BackdropTicks = ChromeTicks = ContentTicks = ShellTicks = SampleTicks = BlurTicks = RangeTicks = 0;
        return text;
    }
    /// <summary>Per compose since the last call: lower pass, top pass and the phases above (microseconds).</summary>
    public static string TakeComposePhases()
    {
        long n = Math.Max(1, ComposeCount), per = n * Math.Max(1, Stopwatch.Frequency / 1_000_000);
        string text = "composes=" + ComposeCount + " full=" + FullComposes + " lower=" + LowerTicks / per + " top=" + TopTicks / per + " " + TakePhases((int)n);
        LowerTicks = TopTicks = ComposeCount = FullComposes = 0;
        return text;
    }

    public static void RecordCompose(long start) { SceneComposes++; LastComposeMicros = Micros(start); MaxComposeMicros = Math.Max(MaxComposeMicros, LastComposeMicros); }

    public static void RecordPresent(long start) { Frames++; LastPresentMicros = Micros(start); MaxPresentMicros = Math.Max(MaxPresentMicros, LastPresentMicros); }

    public static void RecordSceneFrame(long start) => AddSceneFrame(Micros(start));

    public static void AddSceneFrame(long micros)
    {
        SceneFrames++; TotalFrameMicros += micros; MaxFrameMicros = Math.Max(MaxFrameMicros, micros);
        LifetimeSceneFrames++; LifetimeFrameMicros += micros; s_monitorWorst = Math.Max(s_monitorWorst, micros);
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
