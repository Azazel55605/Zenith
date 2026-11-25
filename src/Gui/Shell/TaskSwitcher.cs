using System;
using System.Collections.Generic;
using Zenith.Core.Configuration;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

internal sealed class TaskSwitcher
{
    private readonly List<Window> _windows = new();
    private int _selected;
    private bool _list;
    public bool Open { get; private set; }

    public bool Start(WindowManager manager, DesktopPreferences preferences, int step)
    {
        manager.SwitcherCandidates(_windows, preferences);
        if (_windows.Count == 0) { return false; }
        _list = preferences.SwitcherLayout == 1;
        _selected = _windows.IndexOf(manager.Focused!);
        if (_selected < 0) { _selected = step > 0 ? _windows.Count - 1 : 0; }
        Open = true;
        Step(step);
        return true;
    }

    public bool Prune()
    {
        if (!Open) { return false; }
        var selected = _windows[_selected];
        int removed = _windows.RemoveAll(w => w.IsClosed);
        if (_windows.Count == 0) { Cancel(); return true; }
        int at = _windows.IndexOf(selected);
        _selected = at < 0 ? Math.Min(_selected, _windows.Count - 1) : at;
        return removed > 0;
    }

    public void Step(int direction)
    {
        if (Open) { _selected = (_selected + direction % _windows.Count + _windows.Count) % _windows.Count; }
    }

    public void Accept(WindowManager manager)
    {
        if (!Open) { return; }
        Window chosen = _windows[_selected];
        Cancel();
        manager.Activate(chosen);
    }

    public void Cancel() { Open = false; _windows.Clear(); }

    private (Rect Panel, int First, int Visible) Layout(int width, int height)
    {
        int size = _list ? Math.Min(width - 32, 520) : Math.Min(width - 32, 828);
        int visible = Math.Min(_windows.Count, _list ? Math.Max(1, Math.Min(7, (height - 100) / 52)) : Math.Max(1, (size - 24) / 123));
        if (!_list) { size = Math.Min(size, visible * 162 + 24); }
        int tall = _list ? 66 + visible * 52 : 160;
        int first = Math.Clamp(_selected - visible / 2, 0, _windows.Count - visible);
        return (new Rect((width - size) / 2, (height - tall) / 2, size, tall), first, visible);
    }

    private Rect Item(Rect box, int slot, int visible)
    {
        if (_list) { return new(box.X + 12, box.Y + 44 + slot * 52, box.W - 24, 46); }
        int width = (box.W - 24) / visible;
        return new(box.X + 12 + slot * width, box.Y + 44, width - 6, 88);
    }

    public void Click(WindowManager manager, int x, int y, int width, int height)
    {
        if (!Open) { return; }
        var (box, first, visible) = Layout(width, height);
        for (int i = 0; i < visible; i++)
        {
            if (Item(box, i, visible).Contains(x, y)) { _selected = first + i; Accept(manager); return; }
        }
        if (!box.Contains(x, y)) { Cancel(); }
    }

    public void Draw(Surface surface, WindowManager manager)
    {
        if (!Open) { return; }
        var (box, first, visible) = Layout(surface.Width, surface.Height);
        surface.DrawShadow(box, 16, Theme.ShadowSize, Theme.ShadowAlpha, 5);
        surface.FillRoundRect(box, 16, Theme.Panel);
        surface.DrawText("Switch windows", Fonts.SemiBold, 15, Theme.TextPrimary, box.X + 16, box.Y + 14);
        string position = (_selected + 1) + " / " + _windows.Count;
        surface.DrawText(position, Fonts.Regular, 12, Theme.TextSecondary, box.Right - 16 - Fonts.Regular.MeasureString(position, 12), box.Y + 17);
        for (int i = 0; i < visible; i++)
        {
            int at = first + i;
            Window window = _windows[at];
            Rect item = Item(box, i, visible);
            bool selected = _selected == at;
            surface.FillRoundRect(item, 8, selected ? Theme.AccentSoft : Theme.Raised);
            int icon = _list ? 18 : 22, iconX = item.X + 10, iconY = item.Y + 8;
            uint ink = selected ? Theme.Accent : Theme.TextSecondary;
            if (!Icons.Draw(surface, window.IconName, iconX, iconY, icon, ink)) { surface.FillRoundRect(new Rect(iconX + 2, iconY + 3, icon - 4, icon - 6), 3, ink); }
            int labelX = _list ? iconX + icon + 8 : iconX;
            int labelY = _list ? item.Y + 5 : item.Y + 37;
            surface.Clip = item.Inflate(-4);
            surface.DrawText(window.Title, Fonts.SemiBold, 13, Theme.TextPrimary, labelX, labelY);
            string detail = manager.DesktopName(window.WorkspaceId);
            if (window.IsMinimized) { detail += " · Minimized"; }
            surface.DrawText(detail, Fonts.Regular, 11, Theme.TextSecondary, labelX, labelY + 18);
            surface.ResetClip();
        }
    }
}
