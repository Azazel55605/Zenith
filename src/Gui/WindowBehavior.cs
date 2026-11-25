using System.Diagnostics;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

internal readonly record struct WindowBehavior(uint FocusMode, uint FocusDelay, uint RaiseOnClick,
    uint RaiseOnFocus, uint TitleDoubleClick, uint MaximizeButtonAction, uint ScreenSnap, uint WindowSnapDistance)
{
    internal static WindowBehavior Default => new(0, 250, 1, 0, 0, 0, 1, 0);
}

internal sealed partial class WindowManager
{
    private WindowBehavior _behavior = WindowBehavior.Default;
    private Window? _hoverFocus;
    private long _hoverFocusStart;

    public void SetBehavior(WindowBehavior behavior)
    {
        if (_behavior == behavior) { return; }
        _behavior = behavior;
        _lastTitleClick = null;
        CancelPointerFocus();
        if (behavior.ScreenSnap == 0)
        {
            _snapZone = SnapNone;
            _snapStart = 0;
        }
    }

    private void CancelPointerFocus() => _hoverFocus = null;

    /// <summary>Only actual pointer movement starts a focus delay; idle ticks finish it.</summary>
    public bool UpdatePointerFocus(int x, int y, bool eligible, bool moved)
    {
        if (!eligible || IsDragging || _behavior.FocusMode == 0)
        {
            CancelPointerFocus();
            return false;
        }
        if (!moved && _hoverFocus is null) { return false; }
        Window? hit = HitTest(x, y, includeResizeMargin: false);
        if (hit is null || hit.IsClosed || hit.IsFocused || !IsOnActiveDesktop(hit))
        {
            CancelPointerFocus();
            return false;
        }
        long now = Stopwatch.GetTimestamp();
        if (hit != _hoverFocus)
        {
            CancelPointerFocus();
            if (!moved) { return false; }
            _hoverFocus = hit;
            _hoverFocusStart = now;
        }
        if (now - _hoverFocusStart < (long)_behavior.FocusDelay * Stopwatch.Frequency / 1000) { return false; }
        Focus(hit, _behavior.RaiseOnFocus != 0);
        return true;
    }

    private void RunTitleAction(Window window, uint action)
    {
        switch (action)
        {
            case 0: ToggleMaximize(window); break;
            case 1: Minimize(window); break;
            case 2: ToggleFullscreen(window); break;
        }
    }

    private Rect SnapToWindows(Window window, Rect moving)
    {
        int distance = (int)_behavior.WindowSnapDistance;
        if (distance == 0) { return moving; }
        int dx = distance + 1, dy = distance + 1;
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window other = _windows[i];
            if (other == window || other.IsMinimized || other.IsClosed || other.IsFullscreen
                || other.IsMaximized || !IsOnActiveDesktop(other)) { continue; }
            WindowSnap.NearestContact(moving, other.Bounds, ref dx, ref dy);
        }
        return moving.Offset(dx == distance + 1 ? 0 : dx, dy == distance + 1 ? 0 : dy);
    }
}
