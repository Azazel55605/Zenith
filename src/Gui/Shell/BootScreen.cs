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

    public static bool Shown { get; private set; }

    public static bool Wanted(string arguments)
    {
        foreach (string word in arguments.Split(' '))
        {
            if (word is "session=headless" or "nosplash") { return false; }
        }
        return true;
    }

    public static void Begin()
    {
        try
        {
            // Physical pixels: drawn at the screen's own resolution, not enlarged from the logical size.
            var screen = SessionDisplay.Physical;
            s_surface = new Surface(screen.Width, screen.Height);
            Shown = true;
            Draw(null);
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
            s_done++;
            if (!s_fonts && s_done > 1) { Fonts.Load(); s_fonts = true; }
            Draw(s_fonts ? name : null);
        }
        catch (Exception e) { Stop("boot screen stopped: " + e.Message); }
    }

    /// <summary>Hands the last frame to the crossfade into the first session.</summary>
    public static void Finish()
    {
        if (s_surface is null) { return; }
        SessionDisplay.KeepFrame(s_surface.Pixels, s_surface.Width, s_surface.Height);
        s_surface = null;
    }

    /// <summary>One boot or power frame: logo, then the label and how much of the bar is filled, 0-256.</summary>
    /// <summary>One boot or power frame: logo, then the label and how much of the bar is filled, 0-256.
    /// Sizes follow the surface's height (1080 lines draw them at 100%) and never fall below the display
    /// <paramref name="percent"/> of a physical surface, so the frame looks the same on any screen, matches
    /// the scaled desktop, and stays sharp at high resolutions.</summary>
    public static void Paint(Surface surface, string? label, int filled, int percent = 100)
    {
        surface.FillRect(surface.Bounds, Background);
        int unit = Math.Clamp(Math.Max(surface.Height * 1000 / 1080, percent * 10), 1000, 4000);
        int S(int value) => value * unit / 1000;
        int x = surface.Width / 2, y = surface.Height / 2, logo = S(LogoSize), barWidth = S(BarWidth), barHeight = Math.Max(BarHeight, S(BarHeight));
        Logo.Draw(surface, new Rect(x - logo / 2, y - logo - S(16), logo, logo));
        if (label is not null)
        { surface.DrawTextCentered(label, Fonts.Regular, S(Theme.TextBody), 0xFFB4B7C0, new Rect(0, y + S(8), surface.Width, S(24)), true); }
        if (filled < 0) { return; }
        Rect bar = new(x - barWidth / 2, y + S(44), barWidth, barHeight);
        surface.FillRoundRect(bar, barHeight / 2, 0xFF2A2C31);
        int width = barWidth * filled / 256;
        if (width >= barHeight) { surface.FillRoundRect(new Rect(bar.X, bar.Y, width, barHeight), barHeight / 2, Theme.Accent); }
    }

    private static void Draw(string? label)
    {
        Paint(s_surface!, label, Math.Min(s_done, Phases) * 256 / Phases, SessionDisplay.Percent);
        var screen = SessionDisplay.Physical;
        // The mode can change before the first session (saved preferences): draw for the new one.
        if (screen.Width != s_surface!.Width || screen.Height != s_surface.Height)
        {
            s_surface = new Surface(screen.Width, screen.Height);
            Paint(s_surface, label, Math.Min(s_done, Phases) * 256 / Phases, SessionDisplay.Percent);
        }
        FramePresenter.PresentFrame(screen, s_surface.Pixels, s_surface.Width, s_surface.Height);
    }

    private static void Stop(string message)
    {
        s_surface = null;
        Zenith.Core.Log.Write("boot", message);
    }
}
