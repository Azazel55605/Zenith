using System;
using System.Collections.Generic;
using Zenith.Core.Time;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>
/// The panel along a screen edge with the launcher, workspace pager, pinned apps, open windows and the clock.
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
    private readonly List<Window> _unpinned = new();
    private readonly List<(Rect Rect, int Index)> _desktops = new();
    private sealed record Pin(string Id, string Label, string Icon);
    private readonly List<Pin> _pins = new();
    private readonly List<(Rect Rect, Pin App, Window? Window)> _pinButtons = new();
    private readonly List<(Pin App, Window? Window)> _overflowPins = new();
    private string[]? _pinIds;
    private readonly HostMaterial _material = new();
    private Rect _overflowButton;
    private int _overflowStart;
    public void Dispose() => _material.Dispose();
    private string _time = string.Empty;
    private string _date = string.Empty;

    public Taskbar(int screenWidth, int screenHeight, WindowManager windows)
    {
        _windows = windows;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    /// <summary>Docked runs edge to edge with square corners. Floating is inset and rounded.</summary>
    public bool Docked { get; set; }
    public TaskbarPosition Position { get; set; }
    public Func<string, bool>? LaunchPin { get; set; }
    public bool SetPins(string[] ids)
    {
        if (ReferenceEquals(ids, _pinIds)) { return false; }
        _pinIds = ids;
        _pins.Clear();
        if (ids.Length > 0)
        {
            var apps = AppRegistry.Visible();
            foreach (string id in ids)
            {
                AppInfo? app = apps.Find(candidate => string.Equals(candidate.Name, id, StringComparison.OrdinalIgnoreCase));
                _pins.Add(new Pin(id, app?.DisplayName ?? id, app?.Icon ?? AppRegistry.IconFor(id)));
            }
        }
        return true;
    }
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
        return TaskbarPolicy.Bounds(new Rect(0, 0, _screenWidth, _screenHeight), Position, Theme.TaskbarHeight, margin);
    }

    public static int RadiusFor(bool docked) => docked ? 0 : Theme.WindowRadius;
    public bool Shown { get; set; } = true;
    public bool OverflowOpen { get; set; }
    /// <summary>The network and sound centers beside the clock (the desktop sets them; previews have none).</summary>
    public TaskbarCenters? Centers { get; set; }
    /// <summary>The overflow list or a center's panel is open: drawn over the scene, closed by Escape.</summary>
    public bool PopupOpen => OverflowOpen || (Centers is not null && Centers.Open != TaskbarCenters.Panel.None);
    public void ClosePopups() { OverflowOpen = false; if (Centers is not null && Centers.Open != TaskbarCenters.Panel.None) { Centers.Open = TaskbarCenters.Panel.None; Zenith.Core.Log.Write("taskbar", "centers closed"); } }
    public int OverflowCount => _overflow.Count + _overflowPins.Count;
    public Rect OverflowButton => _overflowButton;

    /// <summary>Set by the desktop so the launcher button shows its pressed state.</summary>
    public bool LauncherOpen { get; set; }
    private long _clockMinute = long.MinValue, _centerSecond, _centerKey;

    /// <summary>Refreshes the clock text; returns true when it changed.</summary>
    public bool Update()
    {
        DateTime now = SystemClock.Now;
        // An open center shows live traffic and levels: redraw once a second while one is open.
        long second = now.Ticks / TimeSpan.TicksPerSecond;
        bool live = Centers is not null && Centers.Open != TaskbarCenters.Panel.None && second != _centerSecond;
        // Once a second, also when closed: the buttons' state (online, mute, volume) may change from elsewhere.
        if (Centers is not null && second != _centerSecond) { long key = Centers.StateKey(); if (key != _centerKey) { _centerKey = key; live = true; } }
        _centerSecond = second;
        long minute = now.Ticks / TimeSpan.TicksPerMinute;
        if (minute == _clockMinute) { return live; }
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
            if (inList)
            {
                int index = ListIndexAt(y);
                if (index >= 0 && index < _overflowPins.Count)
                {
                    var pin = _overflowPins[index];
                    ActivatePin(pin.App, pin.Window);
                }
                else if (index >= _overflowPins.Count && index < OverflowCount) { Activate(_overflow[index - _overflowPins.Count]); }
            }
            bool onButton = _overflowButton.Contains(x, y);
            OverflowOpen = false;
            if (inList || onButton) { return true; }
        }
        else if (OverflowCount > 0 && _overflowButton.Contains(x, y))
        {
            _overflowStart = 0;
            OverflowOpen = true; Zenith.Gui.Motion.OverlayMotion.Start(Zenith.Gui.Motion.OverlayMotion.Overflow);
            return true;
        }

        if (Centers is not null)
        {
            bool wasOpen = Centers.Open != TaskbarCenters.Panel.None;
            if (Centers.HandleClick(x, y)) { return true; }
            if (wasOpen && !Bounds.Contains(x, y)) { return true; }   // the click only closed the panel
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

        foreach (var (rect, index) in _desktops)
        {
            if (!rect.Contains(x, y)) { continue; }
            _windows.SwitchDesktop(index);
            return true;
        }
        foreach (var (rect, app, window) in _pinButtons)
        {
            if (!rect.Contains(x, y)) { continue; }
            ActivatePin(app, window);
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
        if (window.IsClosed || !_windows.IsOnActiveDesktop(window)) { return; }
        if (window.IsFocused && !window.IsMinimized)
        {
            _windows.Minimize(window);
        }
        else
        {
            _windows.Focus(window);
        }
    }

    private void ActivatePin(Pin app, Window? window)
    {
        if (window is not null && !window.IsClosed && _windows.IsOnActiveDesktop(window)) { Activate(window); }
        else { LaunchPin?.Invoke(app.Id); }
    }

    private Window? PinWindow(string id)
    {
        IReadOnlyList<Window> windows = _windows.Windows;
        if (_windows.Focused is Window focused && string.Equals(focused.AppId, id, StringComparison.OrdinalIgnoreCase)) { return focused; }
        for (int index = windows.Count - 1; index >= 0; index--)
        {
            Window window = windows[index];
            if (!window.IsClosed && _windows.IsOnActiveDesktop(window) && string.Equals(window.AppId, id, StringComparison.OrdinalIgnoreCase)) { return window; }
        }
        return null;
    }

    public bool ScrollOverflow(int x, int y, int delta)
    {
        if (!Shown || !OverflowOpen || !ListBounds().Contains(x, y)) { return false; }
        _overflowStart = Math.Clamp(_overflowStart + Math.Sign(delta) * 3, 0, OverflowCount - OverflowVisible);
        return true;
    }

    /// <summary>Where the last layout put this window's pill; null when it is in the overflow list.</summary>
    public Rect? PillOf(Window window)
    {
        foreach (var (rect, _, w) in _pinButtons) { if (w == window) { return rect; } }
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

        foreach (var (rect, index) in _desktops) { if (rect.Contains(x, y)) { return 600 + index; } }
        for (int i = 0; i < _pinButtons.Count; i++) { if (_pinButtons[i].Rect.Contains(x, y)) { return 700 + i; } }

        if (Centers is not null && Centers.HoverKey(x, y) is int center && center != 0) { return center; }

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
        _desktops.Clear();
        _pinButtons.Clear();
        _overflowPins.Clear();
        _overflowButton = new Rect(0, 0, 0, 0);

        int left = LauncherButton.Right + 10;
        int right = Centers is null ? ClockLeft() : Centers.Layout(ClockLeft() - 12, LauncherButton.Y, LauncherButton.H) - 4;
        right -= 16;
        int desktopCount = _windows.DesktopCount, pagerWidth = TaskbarLayout.PagerWidth(desktopCount, right - left);
        if (pagerWidth > 0)
        {
            for (int index = 0; index < desktopCount; index++)
            {
                _desktops.Add((new Rect(left, LauncherButton.Y, pagerWidth, LauncherButton.H), index));
                left += pagerWidth + TaskbarLayout.PagerGap;
            }
            left += 7;
        }
        const int pinWidth = 34;
        foreach (Pin app in _pins)
        {
            Window? window = PinWindow(app.Id);
            if (right - left < pinWidth + TaskbarLayout.OverflowWidth + 2 * TaskbarLayout.Gap)
            { _overflowPins.Add((app, window)); continue; }
            _pinButtons.Add((new Rect(left, LauncherButton.Y, pinWidth, LauncherButton.H), app, window));
            left += pinWidth + TaskbarLayout.Gap;
        }
        if (_pinButtons.Count > 0) { left += 4; }
        int available = right - left;
        IReadOnlyList<Window> open = _windows.OpenOrder;
        _unpinned.Clear();
        foreach (Window window in open)
        {
            if (!window.IsClosed && _windows.IsOnActiveDesktop(window) && !IsPinnedWindow(window)) { _unpinned.Add(window); }
        }
        int shown = TaskbarLayout.Count(_unpinned.Count, available, _overflowPins.Count > 0);
        var pills = new List<Window>(shown);
        for (int i = 0; i < _unpinned.Count; i++)
        {
            if (i < shown) { pills.Add(_unpinned[i]); }
            else { _overflow.Add(_unpinned[i]); }
        }

        // The focused window keeps a pill; the one it replaces moves to the list.
        if (shown > 0 && _windows.Focused is Window focused && _overflow.Remove(focused))
        {
            _overflow.Insert(0, pills[shown - 1]);
            pills[shown - 1] = focused;
        }

        int room = available;
        if (OverflowCount > 0)
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

        if (OverflowCount > 0)
        {
            // Beside the clock: pills move when a title changes, and this button must not.
            _overflowButton = new Rect(right - TaskbarLayout.OverflowWidth, LauncherButton.Y, TaskbarLayout.OverflowWidth, LauncherButton.H);
            _overflowStart = Math.Min(_overflowStart, OverflowCount - OverflowVisible);
        }
    }

    private bool IsPinnedWindow(Window window)
    {
        foreach (var pin in _pinButtons) { if (pin.Window == window) { return true; } }
        foreach (var pin in _overflowPins) { if (pin.Window == window) { return true; } }
        return false;
    }

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        Layout();
        _material.Capture(surface, Bounds, Radius, Theme.Panel);
        if (!Docked) { surface.DrawShadow(Bounds, Radius, 14, 70, 3); }
        _material.Paint(surface, Bounds, Theme.Panel);
        if (Docked) { surface.FillRect(new Rect(Bounds.X, Position == TaskbarPosition.Top ? Bounds.Bottom - 1 : Bounds.Y, Bounds.W, 1), Theme.PanelBorder); }
        else { surface.StrokeRoundRect(Bounds, Radius, Theme.PanelBorder); }

        DrawLauncherButton(surface, mouseX, mouseY);
        DrawClock(surface);
        Centers?.DrawButtons(surface, mouseX, mouseY);
        foreach (var (rect, index) in _desktops)
        {
            bool current = index == _windows.ActiveDesktopIndex;
            if (current || rect.Contains(mouseX, mouseY)) { surface.FillRoundRect(rect, Theme.ControlRadius, current ? Theme.AccentSoft : Theme.Hover); }
            surface.DrawTextCentered((index + 1).ToString(), Fonts.SemiBold, Theme.TextTitle, current ? Theme.Accent : Theme.TextPrimary, rect, true);
        }
        foreach (var (rect, app, window) in _pinButtons) { DrawPin(surface, rect, app, window, mouseX, mouseY); }
        foreach (var (rect, window) in _pills) { DrawPill(surface, rect, window, mouseX, mouseY); }
        if (OverflowCount > 0) { DrawOverflowButton(surface, mouseX, mouseY); }
    }

    /// <summary>Draws the list of windows that did not fit, on top of everything else.</summary>
    public void DrawOverflowList(Surface surface, int mouseX, int mouseY)
    {
        Centers?.DrawPanel(surface, Bounds, Position, mouseX, mouseY);
        if (!OverflowOpen || OverflowCount == 0) { return; }
        Rect list = ListBounds().Offset(0, TaskbarPolicy.SlideAway(Position, Zenith.Gui.Motion.OverlayMotion.Offset(Zenith.Gui.Motion.OverlayMotion.Overflow)));
        surface.DrawShadow(list, Theme.WindowRadius, 18, 100, 6);
        surface.FillRoundRect(list, Theme.WindowRadius, Theme.Menu);
        surface.StrokeRoundRect(list, Theme.WindowRadius, Theme.PanelBorder);
        for (int i = _overflowStart; i < _overflowStart + OverflowVisible; i++)
        {
            Rect row = new(list.X + ListPadding, list.Y + ListPadding + (i - _overflowStart) * Theme.ShellControlHeight, list.W - 2 * ListPadding, Theme.ShellControlHeight);
            if (row.Contains(mouseX, mouseY)) { surface.FillRoundRect(row, Theme.ControlRadius, Theme.Hover); }
            if (i < _overflowPins.Count)
            {
                Pin app = _overflowPins[i].App;
                DrawIconAndTitle(surface, row, app.Icon, app.Label, Theme.TextPrimary);
            }
            else { DrawIconAndTitle(surface, row, _overflow[i - _overflowPins.Count], Theme.TextPrimary); }
        }
        if (OverflowCount > OverflowVisible)
        {
            int height = list.H - 2 * ListPadding;
            int thumb = height * OverflowVisible / OverflowCount;
            surface.FillRoundRect(new Rect(list.Right - 4, list.Y + ListPadding + height * _overflowStart / OverflowCount, 3, thumb), 1, Theme.TextMuted);
        }
    }

    private (Rect Button, string Label)? HoverLabel(int x, int y)
    {
        if (!Shown || PopupOpen || LauncherOpen) { return null; }
        foreach (var (rect, index) in _desktops)
        { if (rect.Contains(x, y) && index < _windows.DesktopCount) { return (rect, _windows.DesktopNameAt(index)); } }
        foreach (var (rect, app, _) in _pinButtons)
        { if (rect.Contains(x, y)) { return (rect, app.Label); } }
        return null;
    }

    public bool HasHoverLabel(int x, int y) => HoverLabel(x, y) is not null;

    public void DrawHoverLabel(Surface surface, int x, int y)
    {
        if (HoverLabel(x, y) is not { } item) { return; }
        int width = Math.Min(_screenWidth - 2 * Theme.TaskbarMargin, Fonts.Regular.MeasureString(item.Label, Theme.TextSmall) + 20);
        int left = Math.Clamp(item.Button.X + (item.Button.W - width) / 2, Theme.TaskbarMargin, _screenWidth - Theme.TaskbarMargin - width);
        Rect area = new(left, TaskbarPolicy.Inward(Position, Bounds, 30, Theme.TaskbarMargin), width, 30);
        surface.FillRoundRect(area, Theme.ControlRadius, Theme.Menu);
        surface.StrokeRoundRect(area, Theme.ControlRadius, Theme.PanelBorder);
        surface.DrawTextCentered(item.Label, Fonts.Regular, Theme.TextSmall, Theme.TextPrimary, area, true);
    }

    private int OverflowVisible
    {
        get
        {
            int space = Position == TaskbarPosition.Top ? _screenHeight - Bounds.Bottom : Bounds.Y;
            int rows = Math.Max(1, (space - Theme.TaskbarMargin - 2 * ListPadding) / Theme.ShellControlHeight);
            return Math.Min(OverflowCount, rows);
        }
    }

    private Rect ListBounds()
    {
        int height = 2 * ListPadding + OverflowVisible * Theme.ShellControlHeight;
        int x = Math.Min(_overflowButton.X, Bounds.Right - ListWidth);
        return new Rect(x, TaskbarPolicy.Inward(Position, Bounds, height, Theme.TaskbarMargin), ListWidth, height);
    }

    private int ListIndexAt(int y)
    {
        Rect list = ListBounds();
        int row = y - list.Y - ListPadding;
        return row < 0 || row / Theme.ShellControlHeight >= OverflowVisible ? -1 : _overflowStart + row / Theme.ShellControlHeight;
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

    private static void DrawPin(Surface surface, Rect button, Pin app, Window? window, int mouseX, int mouseY)
    {
        bool active = window is { IsFocused: true, IsMinimized: false };
        surface.FillRoundRect(button, Theme.ControlRadius, active ? Theme.AccentSoft : button.Contains(mouseX, mouseY) ? Theme.Hover : Theme.Panel);
        uint color = window?.IsMinimized == true ? Theme.TextMuted : Theme.TextPrimary;
        int x = button.X + (button.W - PillIcon) / 2, y = button.Y + (button.H - PillIcon) / 2;
        if (!Icons.Draw(surface, app.Icon, x, y, PillIcon, color))
        { surface.DrawTextCentered(app.Label[..1], Fonts.SemiBold, Theme.TextTitle, color, button, true); }
        if (window is not null)
        {
            int width = active ? 16 : 4;
            surface.FillRoundRect(new Rect(button.X + (button.W - width) / 2, button.Bottom - 3, width, 3), 1, active ? Theme.Accent : Theme.TextMuted);
        }
    }

    private static void DrawIconAndTitle(Surface surface, Rect box, Window w, uint color) => DrawIconAndTitle(surface, box, w.IconName, w.Title, color);

    private static void DrawIconAndTitle(Surface surface, Rect box, string icon, string title, uint color)
    {
        int iconX = box.X + PillPadding, iconY = box.Y + (box.H - PillIcon) / 2;
        if (!Icons.Draw(surface, icon, iconX, iconY, PillIcon, color))
        {
            surface.DrawTextCentered(title.Length > 0 ? title[..1] : "?", Fonts.SemiBold, Theme.TextTitle, color,
                new Rect(iconX, box.Y, PillIcon, box.H), true);
        }

        Rect text = new(iconX + PillIcon + PillPadding - 2, box.Y, box.Right - iconX - PillIcon - 2 * PillPadding + 2, box.H);
        surface.DrawTextCentered(Fitted(title, text.W), Fonts.Regular, Theme.TextTitle, color, text, false);
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

        Zenith.Gui.Motion.CountMotion.Draw(surface, 1, "+" + OverflowCount, Fonts.SemiBold, Theme.TextTitle, Theme.TextPrimary, _overflowButton, true, _overflowButton.Inflate(-4));
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
