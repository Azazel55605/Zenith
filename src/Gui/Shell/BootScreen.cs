using System;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>The logo and the real startup phase from when graphics is up until the first session draws. Never required:
/// any failure leaves the boot running without it. <c>session=headless</c> and <c>nosplash</c> skip it.</summary>
internal static class BootScreen
{
    private const uint Background = 0xFF151619;
    private const int LogoSize = 96, BarWidth = 240, BarHeight = 4, Phases = 5;
    private static Surface? s_surface;
    private static int s_done;
    private static bool s_fonts;
    // The chosen style, from the kernel command line (bootstyle=, written when the setting is saved): the setting
    // itself lives on the installed root, which is only mounted a few phases in.
    private static int s_style;
    private static string? s_label;
    private static int s_previous;          // filled before the current phase, for Quiet's growing line
    private static long s_phaseAt, s_start;
    // A small thread redraws only the area that moves (~25 frames a second) between phases.
    private static readonly object s_gate = new();
    private static volatile bool s_animating;
    private const int FrameMillis = 40, GrowMillis = 600;
    private static long Millis => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

    public static bool Shown { get; private set; }

    public static bool Wanted(string arguments)
    {
        foreach (string word in arguments.Split(' '))
        {
            if (word is "session=headless" or "nosplash") { return false; }
        }
        return true;
    }

    public static void Begin(string arguments)
    {
        try
        {
            // Physical pixels: drawn at the screen's own resolution, not enlarged from the logical size.
            var screen = SessionDisplay.Physical;
            s_surface = new Surface(screen.Width, screen.Height);
            Shown = true;
            s_style = (int)Zenith.Core.Boot.BootLayout.BootStyleOf(arguments);
            s_start = s_phaseAt = Millis;
            Draw(null);
            s_animating = true;
            new System.Threading.Thread(Animate).Start();
            Zenith.Core.Log.Write("boot", "boot screen shown");
        }
        catch (Exception e) { Stop("boot screen unavailable: " + e.Message); }
    }

    /// <summary>The previous phase finished and this one starts. The first has no text: fonts live on the disk it mounts.</summary>
    public static void Phase(string name)
    {
        if (s_surface is null) { return; }
        try
        {
            lock (s_gate)
            {
                s_previous = Math.Min(s_done, Phases) * 256 / Phases;
                s_done++; s_phaseAt = Millis;
                if (!s_fonts && s_done > 1) { Fonts.Load(); s_fonts = true; }
                s_label = s_fonts ? name : null;
                Draw(s_label);
            }
        }
        catch (Exception e) { Stop("boot screen stopped: " + e.Message); }
    }

    /// <summary>Hands the last frame to the crossfade into the first session.</summary>
    public static void Finish()
    {
        s_animating = false;
        lock (s_gate) { }   // a frame in progress ends first
        if (s_surface is null) { return; }
        SessionDisplay.KeepFrame(s_surface.Pixels, s_surface.Width, s_surface.Height);
        s_surface = null;
    }

    /// <summary>One boot or power frame: logo, then the label and how much of the bar is filled, 0-256.</summary>
    /// <summary>One boot or power frame: logo, then the label and how much of the bar is filled, 0-256.
    /// Sizes follow the surface's height (1080 lines draw them at 100%) and never fall below the display
    /// <paramref name="percent"/> of a physical surface, so the frame looks the same on any screen, matches
    /// the scaled desktop, and stays sharp at high resolutions.</summary>
    public static void Paint(Surface surface, string? label, int filled, int percent = 100, int style = -1, long millis = -1, int from = -1)
    {
        surface.FillRect(surface.Bounds, Background);
        int unit = Math.Clamp(Math.Max(surface.Height * 1000 / 1080, percent * 10), 1000, 4000);
        int S(int value) => value * unit / 1000;
        int x = surface.Width / 2, y = surface.Height / 2, logo = S(LogoSize), barWidth = S(BarWidth), barHeight = Math.Max(BarHeight, S(BarHeight));
        Logo.Draw(surface, new Rect(x - logo / 2, y - logo - S(16), logo, logo));
        if (label is not null)
        { surface.DrawTextCentered(label, Fonts.Regular, S(Theme.TextBody), 0xFFB4B7C0, new Rect(0, y + S(8), surface.Width, S(24)), true); }
        if (filled < 0) { return; }
        // Boot styles (docs/design/startup-concept.svg, kernel 0.0.72), read once the installed root is mounted: the
        // first phase always uses Phases. Every style keeps the phase name; Quiet shows it only as a hairline grows.
        if (style < 0) { style = s_style; }
        // millis: time since the boot screen appeared (-1 draws the still picture used by previews and power screens).
        if (style == 1)
        {
            // Orbit: five dots, one per phase done, and a comet circling them.
            int cx = x, cy = y - logo / 2 - S(16), r = logo * 3 / 4, dot = S(8);
            for (int k = 0; k < 5; k++)
            {
                double a = -Math.PI / 2 + k * 2 * Math.PI / 5;
                surface.FillRoundRect(new Rect(cx + (int)(r * Math.Cos(a)) - dot / 2, cy + (int)(r * Math.Sin(a)) - dot / 2, dot, dot), dot / 2, k * 256 < filled * 5 ? Theme.Accent : 0xFF3A3C43);
            }
            if (millis >= 0)
            {
                for (int tail = 0; tail < 6; tail++)
                {
                    double a = -Math.PI / 2 + (millis - tail * 45) % 1600 / 1600.0 * 2 * Math.PI;
                    int size = Math.Max(2, dot / 2 - tail / 2); uint alpha = (uint)(230 - tail * 36);
                    surface.FillRoundRect(new Rect(cx + (int)(r * Math.Cos(a)) - size / 2, cy + (int)(r * Math.Sin(a)) - size / 2, size, size), size / 2, alpha << 24 | 0xF1F2F4);
                }
            }
            return;
        }
        if (style == 2)
        {
            // Pulse: the logo breathes (the background fades over it and back, 1.6 s a breath).
            if (millis >= 0)
            {
                double breath = (1 - Math.Cos(millis % 1600 / 1600.0 * 2 * Math.PI)) / 2;
                uint veil = (uint)(breath * 150);
                surface.FillRect(new Rect(x - logo / 2 - 2, y - logo - S(16) - 2, logo + 4, logo + 4), veil << 24 | (Background & 0xFFFFFF));
            }
            return;
        }
        if (style == 3)
        {
            // Quiet: a hairline at the bottom that grows smoothly from the last phase to this one.
            int now = filled;
            if (millis >= 0 && from >= 0 && from < filled)
            {
                long into = Math.Min(GrowMillis, Math.Max(0, millis));
                int k = Zenith.Gui.Motion.MotionCurve.EaseOut(Zenith.Gui.Motion.MotionCurve.Linear(into, GrowMillis));
                now = from + (filled - from) * k / Zenith.Gui.Motion.MotionCurve.One;
            }
            surface.FillRect(new Rect(0, surface.Height - Math.Max(2, S(2)), surface.Width * now / 256, Math.Max(2, S(2))), 0xFFF1F2F4);
            return;
        }
        Rect bar = new(x - barWidth / 2, y + S(44), barWidth, barHeight);
        surface.FillRoundRect(bar, barHeight / 2, 0xFF2A2C31);
        int width = barWidth * filled / 256;
        if (width >= barHeight) { surface.FillRoundRect(new Rect(bar.X, bar.Y, width, barHeight), barHeight / 2, Theme.Accent); }
        // Phases: a soft light travels along the filled part while a phase runs.
        if (millis >= 0 && width > S(24))
        {
            int sheen = S(40), at = bar.X + (int)(millis % 1400 * (width + sheen) / 1400) - sheen;
            for (int i = 0; i < sheen; i++)
            {
                int px = at + i; if (px < bar.X || px >= bar.X + width) { continue; }
                uint a = (uint)(90 - Math.Abs(i - sheen / 2) * 180 / sheen);
                surface.FillRect(new Rect(px, bar.Y, 1, barHeight), a << 24 | 0xFFFFFF);
            }
        }
    }

    /// <summary>Where the animation of <paramref name="style"/> draws, so a frame redraws and presents only that.</summary>
    private static Rect AnimatedArea(Surface surface, int style, int percent)
    {
        int unit = Math.Clamp(Math.Max(surface.Height * 1000 / 1080, percent * 10), 1000, 4000);
        int S(int value) => value * unit / 1000;
        int x = surface.Width / 2, y = surface.Height / 2, logo = S(LogoSize);
        return style switch
        {
            1 => new Rect(x - logo, y - logo - S(16) - logo / 2 - S(12), logo * 2, logo * 2 + S(24)),
            2 => new Rect(x - logo / 2 - 2, y - logo - S(16) - 2, logo + 4, logo + 4),
            3 => new Rect(0, surface.Height - Math.Max(2, S(2)), surface.Width, Math.Max(2, S(2))),
            _ => new Rect(x - S(BarWidth) / 2, y + S(44), S(BarWidth), Math.Max(BarHeight, S(BarHeight))),
        };
    }

    private static void Animate()
    {
        try
        {
            while (s_animating)
            {
                System.Threading.Thread.Sleep(FrameMillis);
                lock (s_gate)
                {
                    if (!s_animating || s_surface is null) { break; }
                    var screen = SessionDisplay.Physical;
                    if (screen.Width != s_surface.Width || screen.Height != s_surface.Height) { continue; }
                    Rect area = AnimatedArea(s_surface, s_style, SessionDisplay.Percent).Intersect(s_surface.Bounds);
                    if (area.W <= 0 || area.H <= 0) { continue; }
                    // Repaint the frame under a clip (the logo and the label stay as they are outside it).
                    s_surface.BaseClip = area;
                    int filled = Math.Min(s_done, Phases) * 256 / Phases;
                    long now = Millis;
                    Paint(s_surface, s_label, filled, SessionDisplay.Percent, s_style, s_style == 3 ? now - s_phaseAt : now - s_start, s_previous);
                    s_surface.BaseClip = s_surface.Bounds;
                    FramePresenter.Shared.Present(screen, s_surface.Pixels, s_surface.Width, s_surface.Height, area);
                }
            }
        }
        catch (Exception e) { s_animating = false; Zenith.Core.Log.Write("boot", "boot animation stopped: " + e.Message); }
    }

    private static void Draw(string? label)
    {
        Paint(s_surface!, label, Math.Min(s_done, Phases) * 256 / Phases, SessionDisplay.Percent, -1, s_start == 0 ? -1 : Millis - (s_style == 3 ? s_phaseAt : s_start), s_previous);
        var screen = SessionDisplay.Physical;
        // The mode can change before the first session (saved preferences): draw for the new one.
        if (screen.Width != s_surface!.Width || screen.Height != s_surface.Height)
        {
            s_surface = new Surface(screen.Width, screen.Height);
            Paint(s_surface, label, Math.Min(s_done, Phases) * 256 / Phases, SessionDisplay.Percent, -1, s_start == 0 ? -1 : Millis - (s_style == 3 ? s_phaseAt : s_start), s_previous);
        }
        FramePresenter.PresentFrame(screen, s_surface.Pixels, s_surface.Width, s_surface.Height);
    }

    private static void Stop(string message)
    {
        s_animating = false;
        s_surface = null;
        Zenith.Core.Log.Write("boot", message);
    }
}
