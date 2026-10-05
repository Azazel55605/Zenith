using System;
using System.Collections.Generic;
using MyOS.Gui.Graphics;

namespace MyOS.Gui.Shell;

/// <summary>
/// The floating bar along the bottom edge: the launcher button, one pill per open window,
/// and the clock.
/// </summary>
internal sealed class Taskbar
{
    public const int Height = 48;
    public const int Margin = 8;

    private static readonly string[] s_days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] s_months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    private readonly WindowManager _windows;
    private readonly List<(Rect Rect, Window Window)> _pills = new();
    private string _time = string.Empty;
    private string _date = string.Empty;

    public Taskbar(int screenWidth, int screenHeight, WindowManager windows)
    {
        _windows = windows;
        Bounds = new Rect(Margin, screenHeight - Height - Margin, screenWidth - 2 * Margin, Height);
        LauncherButton = new Rect(Bounds.X + 6, Bounds.Y + 6, Height - 12, Height - 12);
    }

    public Rect Bounds { get; }
    public Rect LauncherButton { get; }

    /// <summary>Set by the desktop so the launcher button shows its pressed state.</summary>
    public bool LauncherOpen { get; set; }

    /// <summary>Refreshes the clock text; returns true when it changed.</summary>
    public bool Update()
    {
        DateTime now = DateTime.Now;
        string time = Pad2(now.Hour) + ":" + Pad2(now.Minute);
        if (time == _time)
        {
            return false;
        }

        _time = time;
        _date = s_days[(int)now.DayOfWeek] + " " + now.Day + " " + s_months[now.Month - 1];
        return true;
    }

    /// <summary>Handles a click. Returns true when the click landed on the bar.</summary>
    public bool HandleClick(int x, int y, out bool launcherClicked)
    {
        launcherClicked = false;
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
                if (window.IsFocused && !window.IsMinimized)
                {
                    _windows.Minimize(window);
                }
                else
                {
                    _windows.Focus(window);
                }

                break;
            }
        }

        return true;
    }

    /// <summary>A value that changes whenever the pointer crosses into a different button on the bar.</summary>
    public int HoverKey(int x, int y)
    {
        if (LauncherButton.Contains(x, y))
        {
            return 1;
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

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        surface.DrawShadow(Bounds, 12, 14, 70, 3);
        surface.FillRoundRect(Bounds, 12, Theme.Panel);
        surface.StrokeRoundRect(Bounds, 12, Theme.PanelBorder);

        DrawLauncherButton(surface, mouseX, mouseY);
        int clockLeft = DrawClock(surface);
        DrawWindowPills(surface, clockLeft - 16, mouseX, mouseY);
    }

    private void DrawLauncherButton(Surface surface, int mouseX, int mouseY)
    {
        Rect b = LauncherButton;
        if (LauncherOpen)
        {
            surface.FillRoundRect(b, 8, Theme.AccentSoft);
        }
        else if (b.Contains(mouseX, mouseY))
        {
            surface.FillRoundRect(b, 8, Theme.Hover);
        }

        // A 2x2 grid of rounded tiles.
        const int tile = 7, gap = 3;
        int ox = b.X + (b.W - (2 * tile + gap)) / 2;
        int oy = b.Y + (b.H - (2 * tile + gap)) / 2;
        for (int i = 0; i < 4; i++)
        {
            Rect t = new(ox + (i % 2) * (tile + gap), oy + (i / 2) * (tile + gap), tile, tile);
            surface.FillRoundRect(t, 2, i == 0 ? Theme.Accent : Theme.TextPrimary);
        }
    }

    /// <summary>Draws the clock right-aligned and returns its left edge.</summary>
    private int DrawClock(Surface surface)
    {
        int right = Bounds.Right - 16;
        int timeWidth = Fonts.SemiBold.MeasureString(_time, Theme.TextTitle);
        int dateWidth = Fonts.Regular.MeasureString(_date, Theme.TextSmall);
        int top = Bounds.Y + 7;

        surface.DrawText(_time, Fonts.SemiBold, Theme.TextTitle, Theme.TextPrimary, right - timeWidth, top);
        surface.DrawText(_date, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, right - dateWidth,
            top + Fonts.SemiBold.GetLineHeight(Theme.TextTitle));

        return right - Math.Max(timeWidth, dateWidth);
    }

    private void DrawWindowPills(Surface surface, int maxRight, int mouseX, int mouseY)
    {
        _pills.Clear();
        int x = LauncherButton.Right + 10;
        Rect area = new(x, Bounds.Y, maxRight - x, Bounds.H);
        surface.Clip = area;

        foreach (Window w in _windows.Windows)
        {
            int textWidth = Fonts.Regular.MeasureString(w.Title, Theme.TextTitle);
            Rect pill = new(x, LauncherButton.Y, Math.Min(textWidth + 28, 200), LauncherButton.H);
            if (pill.X >= area.Right)
            {
                break;
            }

            bool active = w.IsFocused && !w.IsMinimized;
            if (active || pill.Contains(mouseX, mouseY))
            {
                surface.FillRoundRect(pill, 8, Theme.Hover);
            }

            surface.DrawTextCentered(w.Title, Fonts.Regular, Theme.TextTitle,
                w.IsMinimized ? Theme.TextMuted : Theme.TextPrimary, pill, true);

            // Indicator under the label: a wide accent bar when active, a dot otherwise.
            int indicatorWidth = active ? 16 : 4;
            surface.FillRoundRect(new Rect(pill.X + (pill.W - indicatorWidth) / 2, pill.Bottom - 3, indicatorWidth, 3), 1,
                active ? Theme.Accent : Theme.TextMuted);

            _pills.Add((pill, w));
            x = pill.Right + 4;
        }

        surface.ResetClip();
    }

    private static string Pad2(int value) => value < 10 ? "0" + value : value.ToString();
}
