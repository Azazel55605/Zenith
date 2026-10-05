using System;
using System.Collections.Generic;
using System.Drawing;
using Cosmos.Kernel.System;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>
/// Last stop for an exception nothing else caught: logs it (serial + /var/log/boot.log),
/// shows it full screen, and waits for R to restart. Never returns.
/// </summary>
internal static class PanicScreen
{
    private const uint Background = 0xFF2A0F16;
    private const uint Accent = 0xFFFF6B7A;

    public static void Show(Exception exception)
    {
        string title = exception.GetType().Name + ": " + exception.Message;
        string[] trace = (exception.StackTrace ?? "(no stack trace available)").Split('\n');

        Log.Write("panic", title);
        foreach (string frame in trace)
        {
            Log.Write("panic", "  " + frame.Trim());
        }

        try
        {
            Draw(title, trace);
        }
        catch (Exception)
        {
            // Our own drawing failed (fonts not loaded, out of memory): fall back to the Canvas basics.
            Canvas canvas = Canvas.GetFullScreen();
            canvas.Clear(Color.FromArgb(unchecked((int)Background)));
            canvas.DrawString("Zenith stopped: " + title, PCScreenFont.DefaultFont, Color.White, 40, 40);
            canvas.DrawString("Press R to restart.", PCScreenFont.DefaultFont, Color.White, 40, 90);
            canvas.Display();
        }

        while (true)
        {
            while (KeyboardManager.TryReadKey(out KeyEvent? key))
            {
                if (key.Key == ConsoleKeyEx.R)
                {
                    Power.Reboot();
                }
            }

            Power.Halt();
        }
    }

    private static void Draw(string title, string[] trace)
    {
        Canvas screen = Canvas.GetFullScreen();
        var surface = new Surface(screen.Width, screen.Height);
        surface.FillRect(surface.Bounds, Background);

        int x = 64, y = 64, width = screen.Width - 2 * x;
        surface.DrawText("Zenith stopped", Fonts.SemiBold, 28, Accent, x, y);
        y += Fonts.SemiBold.GetLineHeight(28) + 16;

        foreach (string line in Fonts.Wrap(title, Fonts.Regular, 16, width))
        {
            surface.DrawText(line, Fonts.Regular, 16, Theme.TextPrimary, x, y);
            y += Fonts.Regular.GetLineHeight(16);
        }

        y += 20;
        int lineHeight = Fonts.Mono.GetLineHeight(13);
        int maxLines = (screen.Height - y - 120) / lineHeight;
        var frames = new List<string>(trace);
        for (int i = 0; i < frames.Count && i < maxLines; i++)
        {
            surface.DrawText(frames[i].Trim(), Fonts.Mono, 13, Theme.TextSecondary, x, y);
            y += lineHeight;
        }

        int bottom = screen.Height - 80;
        surface.DrawText("Details were written to the kernel log (serial port and /var/log/boot.log).",
            Fonts.Regular, 14, Theme.TextSecondary, x, bottom);
        surface.DrawText("Press R to restart.", Fonts.SemiBold, 14, Theme.TextPrimary, x, bottom + 26);

        screen.DrawArray(surface.Pixels, 0, 0, surface.Width, surface.Height);
        screen.Display();
    }
}
