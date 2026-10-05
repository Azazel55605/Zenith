using System;
using System.Collections.Generic;
using System.Diagnostics;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>
/// Owns the open windows: stacking order (last is topmost), focus, moving by the title bar,
/// resizing from the edges and corners, minimize/maximize/close, and drawing each window
/// with its chrome. Double-clicking a title bar toggles maximize; dragging a maximized window
/// restores it under the pointer.
/// </summary>
internal sealed class WindowManager
{
    private const int ResizeMargin = 5;   // grab zone on each side of a window edge
    private const long DoubleClickMilliseconds = 400;

    [Flags]
    private enum Edges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private readonly List<Window> _windows = new();
    private readonly Rect _workArea;
    private int _cascade;

    // Pointer drag in progress: moving (_edges == None) or resizing.
    private Window? _dragging;
    private Edges _edges;
    private Rect _dragStartBounds;
    private int _dragStartX;
    private int _dragStartY;

    // A press in a window's content area: drags and the release go to that window.
    private Window? _contentDrag;

    private Window? _lastTitleClick;
    private long _lastTitleClickTime;

    public WindowManager(Rect workArea)
    {
        _workArea = workArea;
    }

    /// <summary>Open windows, bottom to top.</summary>
    public IReadOnlyList<Window> Windows => _windows;

    public bool IsDragging => _dragging is not null || _contentDrag is not null;

    public Window? Focused => _windows.Count > 0 && _windows[^1].IsFocused ? _windows[^1] : null;

    public void Open(Window window)
    {
        // Center, then step each new window down-right so they do not stack exactly.
        int x = _workArea.X + (_workArea.W - window.Bounds.W) / 2 + _cascade;
        int y = _workArea.Y + (_workArea.H - window.Bounds.H) / 2 + _cascade;
        _cascade = (_cascade + 28) % 140;
        window.SetBounds(new Rect(Math.Max(x, 0), Math.Max(y, 0), window.Bounds.W, window.Bounds.H));

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

    public void ToggleMaximize(Window window)
    {
        if (!window.CanResize)
        {
            return;
        }

        if (window.IsMaximized)
        {
            window.IsMaximized = false;
            window.SetBounds(window.RestoreBounds);
        }
        else
        {
            window.RestoreBounds = window.Bounds;
            window.IsMaximized = true;
            window.SetBounds(_workArea);
        }
    }

    /// <summary>Removes windows that closed themselves; returns true if any window changed.</summary>
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
                ContinueDrag(input.X, input.Y);
            }
            else
            {
                _dragging = null;
            }

            return true;
        }

        if (_contentDrag is not null)
        {
            Window target = _contentDrag;
            if (input.Left)
            {
                if (input.Moved)
                {
                    target.OnMouseDrag(input.X - target.Content.X, input.Y - target.Content.Y);
                }
            }
            else
            {
                _contentDrag = null;
                target.OnMouseUp(input.X - target.Content.X, input.Y - target.Content.Y);
            }
        }

        Window? hit = HitTest(input.X, input.Y, includeResizeMargin: true);

        if (input.LeftPressed && hit is not null)
        {
            Focus(hit);
            Edges edges = EdgesAt(hit, input.X, input.Y);

            if (edges != Edges.None)
            {
                StartDrag(hit, edges, input.X, input.Y);
            }
            else if (hit.CloseButton.Contains(input.X, input.Y))
            {
                hit.Close();
            }
            else if (hit.MaximizeButton.Contains(input.X, input.Y))
            {
                ToggleMaximize(hit);
            }
            else if (hit.MinimizeButton.Contains(input.X, input.Y))
            {
                Minimize(hit);
            }
            else if (hit.TitleBar.Contains(input.X, input.Y))
            {
                long now = Stopwatch.GetTimestamp();
                bool doubleClick = _lastTitleClick == hit
                    && (now - _lastTitleClickTime) * 1000 / Stopwatch.Frequency < DoubleClickMilliseconds;
                _lastTitleClick = doubleClick ? null : hit;
                _lastTitleClickTime = now;

                if (doubleClick)
                {
                    ToggleMaximize(hit);
                }
                else
                {
                    StartDrag(hit, Edges.None, input.X, input.Y);
                }
            }
            else if (hit.Content.Contains(input.X, input.Y))
            {
                hit.OnMouseDown(input.X - hit.Content.X, input.Y - hit.Content.Y);
                _contentDrag = hit;
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

    private void StartDrag(Window w, Edges edges, int x, int y)
    {
        _dragging = w;
        _edges = edges;
        _dragStartBounds = w.Bounds;
        _dragStartX = x;
        _dragStartY = y;
    }

    private void ContinueDrag(int x, int y)
    {
        Window w = _dragging!;
        int dx = x - _dragStartX, dy = y - _dragStartY;

        if (_edges == Edges.None)
        {
            if (w.IsMaximized && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                // Pulling a maximized window by its title restores it under the pointer,
                // at the same relative position along the title bar.
                Rect restore = w.RestoreBounds;
                int grabX = (_dragStartX - w.Bounds.X) * restore.W / Math.Max(1, w.Bounds.W);
                w.IsMaximized = false;
                w.SetBounds(new Rect(x - grabX, w.Bounds.Y, restore.W, restore.H));
                StartDrag(w, Edges.None, x, y);
                return;
            }

            if (!w.IsMaximized)
            {
                MoveTo(w, _dragStartBounds.X + dx, _dragStartBounds.Y + dy);
            }

            return;
        }

        var (minW, minH) = w.MinimumSize;
        Rect b = _dragStartBounds;
        int left = b.X, top = b.Y, right = b.Right, bottom = b.Bottom;
        if ((_edges & Edges.Left) != 0)
        {
            left = Math.Clamp(b.X + dx, _workArea.X, right - minW);
        }

        if ((_edges & Edges.Right) != 0)
        {
            right = Math.Clamp(b.Right + dx, left + minW, _workArea.Right);
        }

        if ((_edges & Edges.Top) != 0)
        {
            top = Math.Clamp(b.Y + dy, _workArea.Y, bottom - minH);
        }

        if ((_edges & Edges.Bottom) != 0)
        {
            bottom = Math.Clamp(b.Bottom + dy, top + minH, _workArea.Bottom);
        }

        w.SetBounds(new Rect(left, top, right - left, bottom - top));
    }

    /// <summary>Which edges a press at (x, y) would resize; none inside the title buttons or for fixed windows.</summary>
    private static Edges EdgesAt(Window w, int x, int y)
    {
        if (!w.CanResize || w.IsMaximized)
        {
            return Edges.None;
        }

        Rect b = w.Bounds;
        Edges edges = Edges.None;
        if (Math.Abs(x - b.X) <= ResizeMargin)
        {
            edges |= Edges.Left;
        }
        else if (Math.Abs(x - (b.Right - 1)) <= ResizeMargin)
        {
            edges |= Edges.Right;
        }

        if (Math.Abs(y - b.Y) <= ResizeMargin)
        {
            edges |= Edges.Top;
        }
        else if (Math.Abs(y - (b.Bottom - 1)) <= ResizeMargin)
        {
            edges |= Edges.Bottom;
        }

        return edges;
    }

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        Window? hovered = _dragging is null ? HitTest(mouseX, mouseY, includeResizeMargin: false) : null;
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
        int radius = w.IsMaximized ? 0 : Theme.WindowRadius;
        if (!w.IsMaximized)
        {
            surface.DrawShadow(b, radius, Theme.ShadowSize, w.IsFocused ? Theme.ShadowAlpha : Theme.ShadowAlpha / 2, 6);
        }

        surface.FillRoundRect(b, radius, Theme.WindowBackground);

        // Title bar: title on the left, minimize / maximize / close on the right.
        Rect title = w.TitleBar;
        surface.FillRect(new Rect(b.X, title.Bottom - 1, b.W, 1), Theme.TitleDivider);
        surface.Clip = new Rect(title.X + 14, title.Y, w.MinimizeButton.X - title.X - 20, title.H);
        surface.DrawTextCentered(w.Title, Fonts.SemiBold, Theme.TextTitle,
            w.IsFocused ? Theme.TextPrimary : Theme.TextSecondary, surface.Clip, false);
        surface.ResetClip();

        DrawTitleButton(surface, w.MinimizeButton, TitleIcon.Minimize, mouseX, mouseY, Theme.Hover, w.IsFocused);
        if (w.CanResize)
        {
            DrawTitleButton(surface, w.MaximizeButton, w.IsMaximized ? TitleIcon.Restore : TitleIcon.Maximize, mouseX, mouseY, Theme.Hover, w.IsFocused);
        }

        DrawTitleButton(surface, w.CloseButton, TitleIcon.Close, mouseX, mouseY, Theme.Danger, w.IsFocused);

        surface.Clip = w.Content.Inflate(-1);
        w.DrawContent(surface, w.Content);
        surface.ResetClip();

        if (!w.IsMaximized)
        {
            surface.StrokeRoundRect(b, radius, w.IsFocused ? Theme.WindowBorderFocused : Theme.WindowBorder);
        }
    }

    private enum TitleIcon
    {
        Minimize,
        Maximize,
        Restore,
        Close,
    }

    private static void DrawTitleButton(Surface surface, Rect r, TitleIcon icon, int mouseX, int mouseY, uint hoverColor, bool focused)
    {
        bool hover = r.Contains(mouseX, mouseY);
        if (hover)
        {
            surface.FillRoundRect(r, Theme.ControlRadius, hoverColor);
        }

        uint color = hover ? Theme.TextPrimary : focused ? Theme.TextSecondary : Theme.TextMuted;
        int cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
        switch (icon)
        {
            case TitleIcon.Minimize:
                surface.FillRect(new Rect(cx - 5, cy, 10, 1), color);
                break;
            case TitleIcon.Maximize:
                surface.StrokeRoundRect(new Rect(cx - 5, cy - 5, 10, 10), 2, color);
                break;
            case TitleIcon.Restore:
                // Two overlapping squares: the back one shows only its top and right edges.
                surface.StrokeRoundRect(new Rect(cx - 6, cy - 3, 9, 9), 2, color);
                surface.FillRect(new Rect(cx - 3, cy - 6, 8, 1), color);
                surface.FillRect(new Rect(cx + 5, cy - 6, 1, 8), color);
                break;
            default:
                surface.DrawTextCentered("×", Fonts.Regular, 18, color, r, true);
                break;
        }
    }

    /// <summary>A value that changes whenever the pointer crosses into a different window or title-bar button.</summary>
    public int HoverKey(int x, int y)
    {
        Window? hit = HitTest(x, y, includeResizeMargin: false);
        if (hit is null)
        {
            return 0;
        }

        int part = hit.CloseButton.Contains(x, y) ? 1 : hit.MaximizeButton.Contains(x, y) ? 2 : hit.MinimizeButton.Contains(x, y) ? 3 : 4;
        return (_windows.IndexOf(hit) + 1) * 8 + part;
    }

    private Window? HitTest(int x, int y, bool includeResizeMargin)
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window w = _windows[i];
            Rect area = includeResizeMargin && w.CanResize && !w.IsMaximized ? w.Bounds.Inflate(ResizeMargin) : w.Bounds;
            if (!w.IsMinimized && area.Contains(x, y))
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
        w.SetBounds(new Rect(x, y, w.Bounds.W, w.Bounds.H));
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
