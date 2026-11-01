using System;

namespace Zenith.Gui.Shell;

internal enum TaskbarMode { DockMaximized, Floating, Docked, HideMaximized, Auto }

/// <summary>What shape the taskbar takes, whether it shows and how much of the screen it keeps.</summary>
internal static class TaskbarPolicy
{
    /// <summary>Rows at the bottom of the screen that maximized windows stay out of.</summary>
    public static int Reserved(TaskbarMode mode, int barHeight, int margin) => mode switch
    {
        TaskbarMode.Floating => barHeight + 2 * margin,
        TaskbarMode.DockMaximized or TaskbarMode.Docked => barHeight,
        _ => 0,
    };

    public static bool Shown(TaskbarMode mode, bool topFullscreen, bool topMaximized, bool pointerNear, bool popupOpen)
    {
        if (popupOpen || pointerNear) { return true; }
        if (topFullscreen) { return false; }
        return mode switch
        {
            TaskbarMode.Auto => false,
            TaskbarMode.HideMaximized => !topMaximized,
            _ => true,
        };
    }

    public static bool Docked(TaskbarMode mode, bool topFullscreen, bool topMaximized)
        => mode == TaskbarMode.Docked || (mode == TaskbarMode.DockMaximized && (topFullscreen || topMaximized));
}

/// <summary>How window pills share the bar: natural width, then evenly narrower, then an overflow button.</summary>
internal static class TaskbarLayout
{
    public const int MinimumPill = 120, Gap = 4, OverflowWidth = 44;

    /// <summary>How many pills get a place; any others go behind the overflow button.</summary>
    public static int Count(int windows, int available)
    {
        if (windows * MinimumPill + Math.Max(0, windows - 1) * Gap <= available) { return windows; }
        return Math.Clamp((available - OverflowWidth) / (MinimumPill + Gap), 0, windows);
    }

    /// <summary>Shrinks the widest pills first until they fit in <paramref name="available"/>.</summary>
    public static int[] Fit(int[] natural, int available)
    {
        int[] widths = (int[])natural.Clone();
        int room = available - Math.Max(0, natural.Length - 1) * Gap;
        int total = 0;
        foreach (int width in natural) { total += width; }
        if (total <= room) { return widths; }

        bool[] settled = new bool[natural.Length];
        int open = natural.Length;
        while (open > 0)
        {
            int share = room / open;
            bool changed = false;
            for (int i = 0; i < natural.Length; i++)
            {
                if (!settled[i] && natural[i] <= share) { settled[i] = true; room -= natural[i]; open--; changed = true; }
            }
            if (changed) { continue; }
            for (int i = 0; i < natural.Length; i++) { if (!settled[i]) { widths[i] = share; } }
            break;
        }
        return widths;
    }
}
