using System;
using System.Collections.Generic;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>
/// Owns the open windows: stacking order (last is topmost), focus, dragging by the title
/// bar, the minimize/close buttons, and drawing each window with its chrome.
/// </summary>
internal sealed class WindowManager
{
    private readonly List<Window> _windows = new();
    private readonly Rect _workArea;
    private Window? _dragging;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private int _cascade;

    public WindowManager(Rect workArea)
    {
        _workArea = workArea;
    }

    /// <summary>Open windows, bottom to top.</summary>
    public IReadOnlyList<Window> Windows => _windows;

    public bool IsDragging => _dragging is not null;

    public Window? Focused => _windows.Count > 0 && _windows[^1].IsFocused ? _windows[^1] : null;

    public void Open(Window window)
    {
        // Center, then step each new window down-right so they do not stack exactly.
        int x = _workArea.X + (_workArea.W - window.Bounds.W) / 2 + _cascade;
        int y = _workArea.Y + (_workArea.H - window.Bounds.H) / 2 + _cascade;
        _cascade = (_cascade + 28) % 140;
        window.Bounds = new Rect(Math.Max(x, 0), Math.Max(y, 0), window.Bounds.W, window.Bounds.H);

        _windows.Add(window);
        Focus(window);
    }

    public void Focus(Window window)
    {
        window.IsMinimized = false;
        _windows.Remove(window);
        _windows.Add(window);
        foreach (Window w in _windows)
        {
            w.IsFocused = w == window;
        }
    }

    public void Minimize(Window window)
    {
        window.IsMinimized = true;
        window.IsFocused = false;
        FocusTopmostVisible();
    }

    /// <summary>Removes windows that closed themselves; returns true if any did.</summary>
    public bool Update()
    {
        bool changed = false;
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window w = _windows[i];
            if (w.IsClosed)
            {
                _windows.RemoveAt(i);
                changed = true;
                continue;
            }

            if (!w.IsMinimized && w.Update())
            {
                changed = true;
            }
        }

        if (changed && Focused is null)
        {
            FocusTopmostVisible();
        }

        return changed;
    }

    /// <summary>Routes pointer and keyboard input. Returns true if the pointer was over a window.</summary>
    public bool HandleInput(Input input)
    {
        if (_dragging is not null)
        {
            if (input.Left)
            {
                MoveTo(_dragging, input.X - _dragOffsetX, input.Y - _dragOffsetY);
            }
            else
            {
                _dragging = null;
            }

            return true;
        }

        Window? hit = HitTest(input.X, input.Y);

        if (input.LeftPressed && hit is not null)
        {
            Focus(hit);

            if (hit.CloseButton.Contains(input.X, input.Y))
            {
                hit.Close();
            }
            else if (hit.MinimizeButton.Contains(input.X, input.Y))
            {
                Minimize(hit);
            }
            else if (hit.TitleBar.Contains(input.X, input.Y))
            {
                _dragging = hit;
                _dragOffsetX = input.X - hit.Bounds.X;
                _dragOffsetY = input.Y - hit.Bounds.Y;
            }
            else
            {
                hit.OnMouseDown(input.X - hit.Content.X, input.Y - hit.Content.Y);
            }
        }

        if (input.Scroll != 0 && hit is not null)
        {
            // Wheel down reports positive deltas; windows take positive as "scroll back".
            hit.OnScroll(-input.Scroll);
        }

        Window? focused = Focused;
        if (focused is not null)
        {
            foreach (var key in input.Keys)
            {
                focused.OnKey(key);
            }
        }

        return hit is not null;
    }

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        Window? hovered = _dragging is null ? HitTest(mouseX, mouseY) : null;
        foreach (Window w in _windows)
        {
            if (!w.IsMinimized)
            {
                DrawWindow(surface, w, w == hovered ? mouseX : -1, mouseY);
            }
        }
    }

    private static void DrawWindow(Surface surface, Window w, int mouseX, int mouseY)
    {
        Rect b = w.Bounds;
        surface.DrawShadow(b, Theme.WindowRadius, Theme.ShadowSize, w.IsFocused ? Theme.ShadowAlpha : Theme.ShadowAlpha / 2, 6);
        surface.FillRoundRect(b, Theme.WindowRadius, Theme.WindowBackground);

        // Title bar: title on the left, minimize and close on the right.
        Rect title = w.TitleBar;
        surface.FillRect(new Rect(b.X, title.Bottom - 1, b.W, 1), Theme.TitleDivider);
        surface.Clip = new Rect(title.X + 14, title.Y, w.MinimizeButton.X - title.X - 20, title.H);
        surface.DrawTextCentered(w.Title, Fonts.SemiBold, Theme.TextTitle,
            w.IsFocused ? Theme.TextPrimary : Theme.TextSecondary, surface.Clip, false);
        surface.ResetClip();

        DrawTitleButton(surface, w.MinimizeButton, "–", mouseX, mouseY, Theme.Hover, w.IsFocused);
        DrawTitleButton(surface, w.CloseButton, "×", mouseX, mouseY, Theme.Danger, w.IsFocused);

        surface.Clip = w.Content.Inflate(-1);
        w.DrawContent(surface, w.Content);
        surface.ResetClip();

        surface.StrokeRoundRect(b, Theme.WindowRadius, w.IsFocused ? Theme.WindowBorderFocused : Theme.WindowBorder);
    }

    private static void DrawTitleButton(Surface surface, Rect r, string glyph, int mouseX, int mouseY, uint hoverColor, bool focused)
    {
        bool hover = r.Contains(mouseX, mouseY);
        if (hover)
        {
            surface.FillRoundRect(r, Theme.ControlRadius, hoverColor);
        }

        uint color = hover ? Theme.TextPrimary : focused ? Theme.TextSecondary : Theme.TextMuted;
        surface.DrawTextCentered(glyph, Fonts.Regular, 18, color, r, true);
    }

    /// <summary>A value that changes whenever the pointer crosses into a different window or title-bar button.</summary>
    public int HoverKey(int x, int y)
    {
        Window? hit = HitTest(x, y);
        if (hit is null)
        {
            return 0;
        }

        int part = hit.CloseButton.Contains(x, y) ? 1 : hit.MinimizeButton.Contains(x, y) ? 2 : 3;
        return (_windows.IndexOf(hit) + 1) * 4 + part;
    }

    private Window? HitTest(int x, int y)
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window w = _windows[i];
            if (!w.IsMinimized && w.Bounds.Contains(x, y))
            {
                return w;
            }
        }

        return null;
    }

    private void MoveTo(Window w, int x, int y)
    {
        // Keep enough of the title bar on screen to grab it again.
        x = Math.Clamp(x, _workArea.X - w.Bounds.W + 120, _workArea.Right - 120);
        y = Math.Clamp(y, _workArea.Y, _workArea.Bottom - Window.TitleBarHeight);
        w.Bounds = new Rect(x, y, w.Bounds.W, w.Bounds.H);
    }

    private void FocusTopmostVisible()
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            if (!_windows[i].IsMinimized)
            {
                Focus(_windows[i]);
                return;
            }
        }
    }
}
