using System;
using System.Collections.Generic;
using Zenith.Core.Time;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>
/// The bar along the bottom edge, floating or docked to it: the launcher button, one pill per open window, a list
/// of the windows that did not fit, and the clock.
/// </summary>
internal sealed class Taskbar
{
    private const int PillIcon = 16, PillPadding = 10, PillMaximum = 200, ListWidth = 260, ListPadding = 6;

    private static readonly string[] s_days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] s_months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private readonly WindowManager _windows;
    private readonly int _screenWidth, _screenHeight;
    private readonly List<(Rect Rect, Window Window)> _pills = new();
    private readonly List<Window> _overflow = new();
    private readonly HostMaterial _material = new();
    private Rect _overflowButton;
    public void Dispose() => _material.Dispose();
    private string _time = string.Empty;
    private string _date = string.Empty;

    public Taskbar(int screenWidth, int screenHeight, WindowManager windows)
    {
        _windows = windows;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    /// <summary>Docked: edge to edge on the bottom row with square corners. Floating: inset, rounded.</summary>
    public bool Docked { get; set; }
    public Rect Bounds => BoundsFor(Docked);
    public int Radius => RadiusFor(Docked);
    public Rect LauncherButton
    {
        get
        {
            int inset = (Theme.TaskbarHeight - Theme.ShellControlHeight) / 2;
            return new Rect(Bounds.X + inset, Bounds.Y + inset, Theme.ShellControlHeight, Theme.ShellControlHeight);
        }
    }

    public Rect BoundsFor(bool docked)
    {
        int margin = docked ? 0 : Theme.TaskbarMargin;
        return new Rect(margin, _screenHeight - Theme.TaskbarHeight - margin, _screenWidth - 2 * margin, Theme.TaskbarHeight);
    }

    public static int RadiusFor(bool docked) => docked ? 0 : Theme.WindowRadius;
    public bool Shown { get; set; } = true;
    public bool OverflowOpen { get; set; }
    public int OverflowCount => _overflow.Count;
    public Rect OverflowButton => _overflowButton;

    /// <summary>Set by the desktop so the launcher button shows its pressed state.</summary>
    public bool LauncherOpen { get; set; }
    private long _clockMinute = long.MinValue;

    /// <summary>Refreshes the clock text; returns true when it changed.</summary>
    public bool Update()
    {
        DateTime now = SystemClock.Now;
        long minute = now.Ticks / TimeSpan.TicksPerMinute;
        if (minute == _clockMinute) { return false; }
        _clockMinute = minute;
        string time = Pad2(now.Hour) + ":" + Pad2(now.Minute);
        if (time == _time)
        {
            return false;
        }

        _time = time;
        _date = s_days[(int)now.DayOfWeek] + " " + now.Day + " " + s_months[now.Month - 1];
        return true;
    }

    /// <summary>Handles a click. Returns true when the click landed on the bar or its list.</summary>
    public bool HandleClick(int x, int y, out bool launcherClicked)
    {
        launcherClicked = false;
        if (!Shown)
        {
            return false;
        }

        if (OverflowOpen)
        {
            bool inList = ListBounds().Contains(x, y);
            if (inList && ListWindowAt(x, y) is Window listed) { Activate(listed); }
            bool onButton = _overflowButton.Contains(x, y);
            OverflowOpen = false;
            if (inList || onButton) { return true; }
        }
        else if (_overflow.Count > 0 && _overflowButton.Contains(x, y))
        {
            OverflowOpen = true;
            return true;
        }

        if (!Bounds.Contains(x, y))
        {
            return false;
        }

        if (LauncherButton.Contains(x, y))
        {
            launcherClicked = true;
            return true;
        }

        foreach (var (rect, window) in _pills)
        {
            if (rect.Contains(x, y))
            {
                Activate(window);
                break;
            }
        }

        return true;
    }

    private void Activate(Window window)
    {
        if (window.IsFocused && !window.IsMinimized)
        {
            _windows.Minimize(window);
        }
        else
        {
            _windows.Focus(window);
        }
    }

    /// <summary>Where the last layout put this window's pill; null when it is in the overflow list.</summary>
    public Rect? PillOf(Window window)
    {
        foreach (var (rect, w) in _pills) { if (w == window) { return rect; } }
        return null;
    }

    /// <summary>A value that changes whenever the pointer crosses into a different button on the bar.</summary>
    public int HoverKey(int x, int y)
    {
        if (!Shown)
        {
            return 0;
        }

        if (OverflowOpen && ListBounds().Contains(x, y))
        {
            return 500 + (y - ListBounds().Y) / Theme.ShellControlHeight;
        }

        if (LauncherButton.Contains(x, y))
        {
            return 1;
        }

        if (_overflowButton.Contains(x, y))
        {
            return 400;
        }

        for (int i = 0; i < _pills.Count; i++)
        {
            if (_pills[i].Rect.Contains(x, y))
            {
                return i + 2;
            }
        }

        return 0;
    }

    /// <summary>Places the pills without drawing, so a hidden bar still knows where a window minimizes to.</summary>
    public void Layout()
    {
        _pills.Clear();
        _overflow.Clear();
        _overflowButton = new Rect(0, 0, 0, 0);

        int left = LauncherButton.Right + 10;
        int available = ClockLeft() - 16 - left;
        IReadOnlyList<Window> open = _windows.OpenOrder;
        int shown = TaskbarLayout.Count(open.Count, available);
        var pills = new List<Window>(shown);
        for (int i = 0; i < open.Count; i++)
        {
            if (i < shown) { pills.Add(open[i]); }
            else { _overflow.Add(open[i]); }
        }

        // The focused window keeps a pill; the one it replaces moves to the list.
        if (shown > 0 && _windows.Focused is Window focused && _overflow.Remove(focused))
        {
            _overflow.Insert(0, pills[shown - 1]);
            pills[shown - 1] = focused;
        }

        int room = available;
        if (_overflow.Count > 0)
        {
            room -= TaskbarLayout.OverflowWidth + TaskbarLayout.Gap;
        }
        else
        {
            OverflowOpen = false;
        }

        int[] natural = new int[pills.Count];
        for (int i = 0; i < natural.Length; i++)
        {
            natural[i] = Math.Min(PillMaximum, PillIcon + 3 * PillPadding - 2 + Fonts.Regular.MeasureString(pills[i].Title, Theme.TextTitle));
        }

        int[] widths = TaskbarLayout.Fit(natural, room);
        int x = left;
        for (int i = 0; i < widths.Length; i++)
        {
            _pills.Add((new Rect(x, LauncherButton.Y, widths[i], LauncherButton.H), pills[i]));
            x += widths[i] + TaskbarLayout.Gap;
        }

        if (_overflow.Count > 0)
        {
            // Beside the clock: pills move when a title changes, and this button must not.
            _overflowButton = new Rect(left + available - TaskbarLayout.OverflowWidth, LauncherButton.Y, TaskbarLayout.OverflowWidth, LauncherButton.H);
        }
    }

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        Layout();
        _material.Capture(surface, Bounds, Radius, Theme.Panel);
        if (!Docked) { surface.DrawShadow(Bounds, Radius, 14, 70, 3); }
        _material.Paint(surface, Bounds, Theme.Panel);
        if (Docked) { surface.FillRect(new Rect(Bounds.X, Bounds.Y, Bounds.W, 1), Theme.PanelBorder); }
        else { surface.StrokeRoundRect(Bounds, Radius, Theme.PanelBorder); }

        DrawLauncherButton(surface, mouseX, mouseY);
        DrawClock(surface);
        foreach (var (rect, window) in _pills) { DrawPill(surface, rect, window, mouseX, mouseY); }
        if (_overflow.Count > 0) { DrawOverflowButton(surface, mouseX, mouseY); }
    }

    /// <summary>Draws the list of windows that did not fit, on top of everything else.</summary>
    public void DrawOverflowList(Surface surface, int mouseX, int mouseY)
    {
        if (!OverflowOpen || _overflow.Count == 0) { return; }
        Rect list = ListBounds();
        surface.DrawShadow(list, Theme.WindowRadius, 18, 100, 6);
        surface.FillRoundRect(list, Theme.WindowRadius, Theme.Menu);
        surface.StrokeRoundRect(list, Theme.WindowRadius, Theme.PanelBorder);
        for (int i = 0; i < _overflow.Count; i++)
        {
            Rect row = new(list.X + ListPadding, list.Y + ListPadding + i * Theme.ShellControlHeight, list.W - 2 * ListPadding, Theme.ShellControlHeight);
            if (row.Contains(mouseX, mouseY)) { surface.FillRoundRect(row, Theme.ControlRadius, Theme.Hover); }
            DrawIconAndTitle(surface, row, _overflow[i], Theme.TextPrimary);
        }
    }

    private Rect ListBounds()
    {
        int height = 2 * ListPadding + _overflow.Count * Theme.ShellControlHeight;
        int x = Math.Min(_overflowButton.X, Bounds.Right - ListWidth);
        return new Rect(x, Bounds.Y - height - Theme.TaskbarMargin, ListWidth, height);
    }

    private Window? ListWindowAt(int x, int y)
    {
        Rect list = ListBounds();
        int index = (y - list.Y - ListPadding) / Theme.ShellControlHeight;
        return y >= list.Y + ListPadding && index >= 0 && index < _overflow.Count ? _overflow[index] : null;
    }

    private void DrawLauncherButton(Surface surface, int mouseX, int mouseY)
    {
        Rect b = LauncherButton;
        if (LauncherOpen)
        {
            surface.FillRoundRect(b, Theme.ControlRadius, Theme.AccentSoft);
        }
        else if (b.Contains(mouseX, mouseY))
        {
            surface.FillRoundRect(b, Theme.ControlRadius, Theme.Hover);
        }

        Logo.Draw(surface, b.Inflate(-8));
    }

    private int ClockLeft()
    {
        int timeWidth = Fonts.SemiBold.MeasureString(_time, Theme.TextTitle);
        int dateWidth = Fonts.Regular.MeasureString(_date, Theme.TextSmall);
        return Bounds.Right - 16 - Math.Max(timeWidth, dateWidth);
    }

    private void DrawClock(Surface surface)
    {
        int right = Bounds.Right - 16;
        int timeWidth = Fonts.SemiBold.MeasureString(_time, Theme.TextTitle);
        int dateWidth = Fonts.Regular.MeasureString(_date, Theme.TextSmall);
        int timeHeight = Fonts.SemiBold.GetLineHeight(Theme.TextTitle);
        int top = Bounds.Y + (Bounds.H - timeHeight - Fonts.Regular.GetLineHeight(Theme.TextSmall)) / 2;

        surface.DrawText(_time, Fonts.SemiBold, Theme.TextTitle, Theme.TextPrimary, right - timeWidth, top);
        surface.DrawText(_date, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, right - dateWidth, top + timeHeight);
    }

    private static void DrawPill(Surface surface, Rect pill, Window w, int mouseX, int mouseY)
    {
        bool active = w.IsFocused && !w.IsMinimized;
        if (active)
        {
            surface.FillRoundRect(pill, Theme.ControlRadius, Theme.AccentSoft);
        }
        else if (pill.Contains(mouseX, mouseY))
        {
            surface.FillRoundRect(pill, Theme.ControlRadius, Theme.Hover);
        }

        DrawIconAndTitle(surface, pill, w, w.IsMinimized ? Theme.TextMuted : Theme.TextPrimary);

        if (active)
        {
            const int indicator = 16;
            surface.FillRoundRect(new Rect(pill.X + (pill.W - indicator) / 2, pill.Bottom - 3, indicator, 3), 1, Theme.Accent);
        }
    }

    private static void DrawIconAndTitle(Surface surface, Rect box, Window w, uint color)
    {
        int iconX = box.X + PillPadding, iconY = box.Y + (box.H - PillIcon) / 2;
        if (!Icons.Draw(surface, w.IconName, iconX, iconY, PillIcon, color))
        {
            surface.DrawTextCentered(w.Title.Length > 0 ? w.Title.Substring(0, 1) : "?", Fonts.SemiBold, Theme.TextTitle, color,
                new Rect(iconX, box.Y, PillIcon, box.H), true);
        }

        Rect text = new(iconX + PillIcon + PillPadding - 2, box.Y, box.Right - iconX - PillIcon - 2 * PillPadding + 2, box.H);
        surface.DrawTextCentered(Fitted(w.Title, text.W), Fonts.Regular, Theme.TextTitle, color, text, false);
    }

    private void DrawOverflowButton(Surface surface, int mouseX, int mouseY)
    {
        if (OverflowOpen)
        {
            surface.FillRoundRect(_overflowButton, Theme.ControlRadius, Theme.AccentSoft);
        }
        else if (_overflowButton.Contains(mouseX, mouseY))
        {
            surface.FillRoundRect(_overflowButton, Theme.ControlRadius, Theme.Hover);
        }

        surface.DrawTextCentered("+" + _overflow.Count, Fonts.SemiBold, Theme.TextTitle, Theme.TextPrimary, _overflowButton, true);
    }

    /// <summary>The title cut to a width, ending in "..." when it had to be cut.</summary>
    private static string Fitted(string title, int width)
    {
        if (Fonts.Regular.MeasureString(title, Theme.TextTitle) <= width) { return title; }
        for (int length = title.Length - 1; length > 0; length--)
        {
            string cut = title.Substring(0, length).TrimEnd() + "...";
            if (Fonts.Regular.MeasureString(cut, Theme.TextTitle) <= width) { return cut; }
        }

        return string.Empty;
    }

    private static string Pad2(int value) => value < 10 ? "0" + value : value.ToString();
}
