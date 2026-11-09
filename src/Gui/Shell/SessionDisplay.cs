using System;
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
    public static Canvas Screen
    {
        get { s_screen ??= DisplayController.Initialize(); DisplayController.LoadPreferences(); return s_screen; }
    }
    public static void ReplaceScreen(Canvas screen) { DropFrame(); s_screen=screen; }
    /// <summary>The device's own pixels, under any scale: boot and power screens draw here.</summary>
    public static Canvas Physical => Screen is DisplayCanvas scaled ? scaled.Physical : Screen;
    /// <summary>The display scale in percent (100 = one device pixel per logical pixel).</summary>
    public static int Percent { get; set; } = 100;
    /// <summary>A device-pixel coordinate in logical pixels.</summary>
    public static int ToLogical(int physical) => physical * 100 / Percent;
    public static void ConfigurePointer()
    {
        Canvas physical=Physical; int width=physical.Width,height=physical.Height;
        Cosmos.Kernel.System.Mouse.MouseManager.SetScreenSize(width,height);
        Cosmos.Kernel.System.Mouse.MouseManager.SetPosition(System.Math.Clamp(Cosmos.Kernel.System.Mouse.MouseManager.X,0,width-1),
            System.Math.Clamp(Cosmos.Kernel.System.Mouse.MouseManager.Y,0,height-1));
    }

    public static bool FadePending => s_fade is not null;

    /// <summary>Remembers the last frame of a stopping session; skipped when fades are off or memory is short.</summary>
    public static void KeepFrame(int[] pixels)
    {
        DropFrame();
        if (Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent == 0) { return; }
        if (MemoryInfo.FreePages < (ulong)(pixels.Length * sizeof(int) * 2 / 4096) + ReservePages) { return; }
        s_fade = (int[])pixels.Clone();
    }

    /// <summary>A frame drawn at the physical size: kept at the logical size, which sessions fade in.</summary>
    public static void KeepFrame(int[] pixels, int width, int height)
    {
        int percent = Percent, logicalWidth = Screen.Width, logicalHeight = Screen.Height;
        if (width == logicalWidth && height == logicalHeight) { KeepFrame(pixels); return; }
        DropFrame();
        if (width < logicalWidth * percent / 100 || height < logicalHeight * percent / 100) { return; }
        if (Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent == 0) { return; }
        if (MemoryInfo.FreePages < (ulong)(logicalWidth * logicalHeight * sizeof(int) * 2 / 4096) + ReservePages) { return; }
        var kept = new int[logicalWidth * logicalHeight];
        for (int y = 0; y < logicalHeight; y++)
        for (int x = 0; x < logicalWidth; x++)
        { kept[y * logicalWidth + x] = pixels[Math.Min(height - 1, (y * percent + percent / 2) / 100) * width + Math.Min(width - 1, (x * percent + percent / 2) / 100)]; }
        s_fade = kept;
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
                FramePresenter.PresentFrame(screen, mix, width, height);
                long took = Now() - frameStart;
                frames++;
                worst = System.Math.Max(worst, took);
                slow = took > SlowFrameMicros ? slow + 1 : 0;
                if (slow >= 2) { end = "OverBudget"; break; }
            }
            System.Array.Clear(mix);
        }
        if (from is not null) { System.Array.Clear(from); }
        FramePresenter.PresentFrame(screen, frame, width, height);
        Zenith.Core.Log.Write("motion", "SessionFade frames=" + frames + " elapsed=" + elapsed + " worst=" + worst + " ended=" + end);
    }

    // What a session shows now, for the fade when the machine goes down. The session sets and clears it.
    public static int[]? LiveFrame { get; set; }
    private const long FarewellMicros = 200_000, FarewellWaitMicros = 300_000, HoldMicros = 3_000_000;
    private static volatile string? s_farewell;
    private static volatile bool s_farewellDone;
    private static long s_holdUntil;
    [System.ThreadStatic] private static bool t_session;

    /// <summary>Marks the thread that draws sessions; only it may touch the screen.</summary>
    public static void MarkSessionThread() => t_session = true;

    /// <summary>Called with the disks safe, just before the power changes. A worker asks the session thread and gives up
    /// after a bound; either way this takes well under a second, and a failure only skips it.</summary>
    public static void Farewell(string label)
    {
        if (t_session) { PlayFarewell(label); return; }
        s_farewellDone = false;
        s_farewell = label;
        long start = Now();
        while (!s_farewellDone && Now() - start < FarewellWaitMicros + FarewellMicros) { System.Threading.Thread.Sleep(5); }
    }

    /// <summary>On the session thread, before each session tick. True while the farewell owns the screen: no tick may
    /// draw over it. The hold ends by itself, so a power call that returned cannot freeze the session.</summary>
    public static bool ServeFarewell()
    {
        if (s_farewell is string label)
        {
            s_farewell = null;
            s_holdUntil = Now() + HoldMicros;
            try { PlayFarewell(label); }
            catch (System.Exception e) { Zenith.Core.Log.Write("power", "farewell skipped: " + e.Message); }
            s_farewellDone = true;
        }
        return s_holdUntil != 0 && Now() < s_holdUntil;
    }

    private static void PlayFarewell(string label)
    {
        Zenith.Gui.Graphics.HardwareCursor.Hide();
        int percent = System.Math.Min(100, Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent);
        int[]? from = LiveFrame;
        if (percent == 0 || from is null) { return; }
        Canvas screen = Screen;
        if (from.Length != screen.Width * screen.Height) { return; }
        if (MemoryInfo.FreePages < (ulong)(from.Length * sizeof(int) * 2 / 4096) + ReservePages) { return; }
        var target = new Zenith.Gui.Graphics.Surface(screen.Width, screen.Height);
        BootScreen.Paint(target, label, -1);
        int[] mix = new int[from.Length];
        long duration = FarewellMicros * percent / 100, start = Now(), elapsed = 0, worst = 0;
        int frames = 0, slow = 0;
        string end = "Finished";
        while ((elapsed = Now() - start) < duration)
        {
            long frameStart = Now();
            PixelBlend.Mix(from, target.Pixels, mix, MotionCurve.EaseOut(MotionCurve.Linear(elapsed, duration)) * 255 / MotionCurve.One);
            FramePresenter.PresentFrame(screen, mix, screen.Width, screen.Height);
            long took = Now() - frameStart;
            frames++;
            worst = System.Math.Max(worst, took);
            slow = took > SlowFrameMicros ? slow + 1 : 0;
            if (slow >= 2) { end = "OverBudget"; break; }
        }
        FramePresenter.PresentFrame(screen, target.Pixels, screen.Width, screen.Height);
        // The held frame at the device's own resolution, so its text is sharp under a scale.
        Canvas physical = Physical;
        if (physical != screen && MemoryInfo.FreePages >= (ulong)(physical.Width * physical.Height * sizeof(int) / 4096) + ReservePages)
        {
            var sharp = new Zenith.Gui.Graphics.Surface(physical.Width, physical.Height);
            BootScreen.Paint(sharp, label, -1, Percent);
            FramePresenter.PresentFrame(physical, sharp.Pixels, physical.Width, physical.Height);
        }
        Zenith.Core.Log.Write("motion", "Farewell frames=" + frames + " elapsed=" + elapsed + " worst=" + worst + " ended=" + end);
    }

    private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp() / System.Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 1_000_000);
}
