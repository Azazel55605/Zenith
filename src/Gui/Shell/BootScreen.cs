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
            var screen = SessionDisplay.Screen;
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
        SessionDisplay.KeepFrame(s_surface.Pixels);
        s_surface = null;
    }

    /// <summary>One boot or power frame: logo, then the label and how much of the bar is filled, 0-256.</summary>
    public static void Paint(Surface surface, string? label, int filled)
    {
        surface.FillRect(surface.Bounds, Background);
        int x = surface.Width / 2, y = surface.Height / 2;
        Logo.Draw(surface, new Rect(x - LogoSize / 2, y - LogoSize - 16, LogoSize, LogoSize));
        if (label is not null)
        { surface.DrawTextCentered(label, Fonts.Regular, Theme.TextBody, 0xFFB4B7C0, new Rect(0, y + 8, surface.Width, 24), true); }
        if (filled < 0) { return; }
        Rect bar = new(x - BarWidth / 2, y + 44, BarWidth, BarHeight);
        surface.FillRoundRect(bar, BarHeight / 2, 0xFF2A2C31);
        int width = BarWidth * filled / 256;
        if (width >= BarHeight) { surface.FillRoundRect(new Rect(bar.X, bar.Y, width, BarHeight), BarHeight / 2, Theme.Accent); }
    }

    private static void Draw(string? label)
    {
        Paint(s_surface!, label, Math.Min(s_done, Phases) * 256 / Phases);
        var screen = SessionDisplay.Screen;
        screen.DrawArray(s_surface!.Pixels, 0, 0, s_surface.Width, s_surface.Height);
        screen.Display();
    }

    private static void Stop(string message)
    {
        s_surface = null;
        Zenith.Core.Log.Write("boot", message);
    }
}
