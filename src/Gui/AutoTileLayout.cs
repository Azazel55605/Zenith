using System;
using System.Collections.Generic;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>Each tile contains a decorated window. An empty slot belongs to another overflow page.</summary>
internal static class AutoTileLayout
{
    internal readonly record struct Limits(int MinWidth, int MinHeight, int MaxWidth, int MaxHeight);

    public static void Arrange(Rect work, IReadOnlyList<Limits> windows, int masterPercent, int innerGap, int outerGap,
        int selected, int lastStack, List<Rect> slots)
    {
        slots.Clear();
        for (int i = 0; i < windows.Count; i++) { slots.Add(default); }
        if (windows.Count == 0 || work.IsEmpty) { return; }
        int stackWidth = 0, stackHeight = 0;
        for (int i = 1; i < windows.Count; i++)
        {
            stackWidth = Math.Max(stackWidth, windows[i].MinWidth);
            stackHeight = Math.Max(stackHeight, windows[i].MinHeight);
        }

        Limits master = windows[0];
        bool across = master.MinWidth + stackWidth <= work.W;
        bool down = master.MinHeight + stackHeight <= work.H;
        if (windows.Count == 1 || (!across && !down))
        {
            int active = Math.Clamp(selected, 0, windows.Count - 1);
            Limits limits = windows[active];
            slots[active] = Fit(Inset(work, outerGap, limits.MinWidth, limits.MinHeight), limits);
            return;
        }

        // Portrait screens prefer a top master. If one axis cannot fit both
        // minimum sizes, split on the other one.
        bool columns = across && (!down || work.W >= work.H);
        work = Inset(work, outerGap,
            columns ? master.MinWidth + stackWidth : Math.Max(master.MinWidth, stackWidth),
            columns ? Math.Max(master.MinHeight, stackHeight) : master.MinHeight + stackHeight);
        int masterMinimum = columns ? master.MinWidth : master.MinHeight;
        int stackMinimum = columns ? stackWidth : stackHeight;
        int span = columns ? work.W : work.H;
        int divider = Math.Min(innerGap, span - masterMinimum - stackMinimum);
        int extent = span - divider;
        int split = Math.Clamp((int)((long)extent * masterPercent / 100),
            masterMinimum, extent - stackMinimum);
        Rect first = columns ? new(work.X, work.Y, split, work.H) : new(work.X, work.Y, work.W, split);
        Rect stack = columns ? new(first.Right + divider, work.Y, extent - split, work.H)
                             : new(work.X, first.Bottom + divider, work.W, extent - split);
        slots[0] = Fit(first, master);

        // Stack pages have only as many windows as fit at their minimum sizes.
        // Keep the current stack page when focus returns to the master.
        int target = Math.Clamp(selected > 0 ? selected : lastStack, 1, windows.Count - 1);
        int available = columns ? stack.H : stack.W;
        int begin = 1;
        while (begin < windows.Count)
        {
            int end = begin, minimum = 0;
            while (end < windows.Count)
            {
                int need = columns ? windows[end].MinHeight : windows[end].MinWidth;
                if (minimum + need > available) { break; }
                minimum += need;
                end++;
            }
            if (end == begin) { return; } // Caller excludes windows larger than the work area.
            if (target >= end) { begin = end; continue; }
            // Compress spacing when needed so a gap never pushes a stack client below its minimum.
            int seams = end - begin - 1;
            int gap = seams == 0 ? 0 : Math.Min(innerGap, (available - minimum) / seams);
            int remaining = available - gap * seams, offset = 0;
            for (int i = begin; i < end; i++)
            {
                int need = columns ? windows[i].MinHeight : windows[i].MinWidth;
                minimum -= need;
                int size = Math.Clamp(remaining / (end - i), need, remaining - minimum);
                Rect cell = columns ? new(stack.X, stack.Y + offset, stack.W, size)
                                    : new(stack.X + offset, stack.Y, size, stack.H);
                slots[i] = Fit(cell, windows[i]);
                remaining -= size;
                offset += size + gap;
            }
            return;
        }
    }

    private static Rect Inset(Rect work, int gap, int minWidth, int minHeight)
    {
        int x = Math.Min(gap, (work.W - minWidth) / 2);
        int y = Math.Min(gap, (work.H - minHeight) / 2);
        return new(work.X + x, work.Y + y, work.W - 2 * x, work.H - 2 * y);
    }

    private static Rect Fit(Rect cell, Limits limits)
        => new(cell.X, cell.Y, Math.Min(cell.W, limits.MaxWidth), Math.Min(cell.H, limits.MaxHeight));

    /// <summary>Prefer a tile sharing a row/column with the focused window, then its nearest edge and center.</summary>
    public static int Neighbor(IReadOnlyList<Rect> slots, int current, int dx, int dy)
    {
        if (current < 0 || current >= slots.Count || slots[current].IsEmpty || (dx == 0) == (dy == 0)) { return -1; }
        Rect from = slots[current];
        bool horizontal = dx != 0, forward = dx > 0 || dy > 0;
        int result = -1;
        (int outside, int distance, int offset) best = (2, int.MaxValue, int.MaxValue);
        for (int i = 0; i < slots.Count; i++)
        {
            Rect to = slots[i];
            if (i == current || to.IsEmpty) { continue; }
            int distance = horizontal ? (forward ? to.X - from.Right : from.X - to.Right)
                                      : (forward ? to.Y - from.Bottom : from.Y - to.Bottom);
            if (distance < 0) { continue; }
            bool overlap = horizontal ? from.Y < to.Bottom && to.Y < from.Bottom : from.X < to.Right && to.X < from.Right;
            int offset = horizontal ? Math.Abs(from.Y + from.Bottom - to.Y - to.Bottom) : Math.Abs(from.X + from.Right - to.X - to.Right);
            var score = (overlap ? 0 : 1, distance, offset);
            if (score.CompareTo(best) < 0) { best = score; result = i; }
        }
        return result;
    }
}
