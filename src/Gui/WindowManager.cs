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
internal enum WindowChange { Opened, Closed, Minimized, Restored, Resized }

/// <summary>A state change the desktop may animate; <see cref="From"/> is where the window was.</summary>
internal readonly record struct WindowTransition(WindowChange Change, Window Window, Rect From);

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

    private readonly struct LowerWindow
    {
        private readonly Window _window;
        private readonly Rect _bounds;
        private readonly bool _focused, _maximized, _fullscreen;
        private readonly string _title;
        private readonly int _button;
        public LowerWindow(Window w, int button) { _window = w; _bounds = w.Bounds; _focused = w.IsFocused; _maximized = w.IsMaximized; _fullscreen = w.IsFullscreen; _title = w.Title; _button = button; }
        public Window Window => _window;
        public Rect Bounds => _bounds;
        public bool Shows(Window w, int button) => _window == w && SameRect(_bounds, w.Bounds) && _focused == w.IsFocused && _maximized == w.IsMaximized && _fullscreen == w.IsFullscreen && _title == w.Title && _button == button;
    }

    private static bool SameRect(Rect a, Rect b) => a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H;

    private readonly List<Window> _windows = new();
    private readonly List<Window> _opened = new();
    // What the cached scene below the top window shows; any difference means drawing it again.
    private readonly List<LowerWindow> _lower = new();
    private bool _lowerDirty = true;
    // Blur caching across lower-scene redraws: screen areas that look different from the last
    // redraw, and which redraw that was. A window keeps its blurred backdrop when its key and
    // bounds are the same and no changed area reaches under it.
    private readonly List<Rect> _changed = new();
    private long _sceneKey = -1, _generation;
    private const int ChangeMargin = 24;
    /// <summary>The changed parts of the scene under a window, in screen coordinates.</summary>
    private static Rect DirtyUnder(Rect bounds, List<Rect> changes)
    {
        Rect dirty = new(0, 0, 0, 0);
        foreach (Rect c in changes) { dirty = dirty.Union(c.Intersect(bounds)); }
        return dirty;
    }
    private readonly List<Rect> _below = new();
    private bool ChangedUnder(Rect bounds)
    {
        Rect area = bounds.Inflate(ChangeMargin);
        foreach (Rect c in _changed) { Rect hit = c.Intersect(area); if (hit.W > 0 && hit.H > 0) { return true; } }
        return false;
    }
    private Rect _workArea;
    private Rect _screen;
    private int _cascade;
    private WindowTransition? _transition;

    // Pointer drag in progress: moving (_edges == None) or resizing.
    private Window? _dragging;
    // ---------- Snapping (docs/design/motion-additions-concept.svg): dragging a window's title to a screen edge
    // shows a tinted preview that grows where it will land (120 ms); releasing there snaps it. Left or right edge:
    // that half; the top edge: maximized; near a corner: that quarter.
    private const int SnapNone = WindowSnap.None, SnapTop = WindowSnap.Top;
    private const long SnapMicros = 120_000;
    private int _snapZone;
    private long _snapStart;
    private Rect _snapFrom;
    /// <summary>The animation setting in percent (0 = Off), set by the desktop.</summary>
    public static System.Func<int> AnimationPercent = () => 100;
    private static long Micros => System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
    private Edges _edges;
    private Rect _dragStartBounds;
    private int _dragStartX;
    private int _dragStartY;

    // A press in a window's content area: drags and the release go to that window.
    private Window? _contentDrag;
    private Window? _pointerWindow;

    private Window? _lastTitleClick;
    private long _lastTitleClickTime;

    public WindowManager(Rect workArea, Rect screen)
    {
        _workArea = workArea;
        _screen = screen;
    }

    /// <summary>Changes the space maximized windows fill and moves those already maximized.</summary>
    public void SetWorkArea(Rect workArea)
    {
        _workArea = workArea;
        foreach (Window w in _windows)
        {
            if (!w.IsMaximized) { continue; }
            var maximum = w.MaximumSize;
            Rect maximized = new(workArea.X, workArea.Y, Math.Min(workArea.W, maximum.Width), Math.Min(workArea.H, maximum.Height));
            if (w.IsFullscreen) { w.WindowedBounds = maximized; }
            else { w.SetBounds(maximized); }
        }
    }

    public void SetDisplayArea(Rect screen, Rect work)
    {
        _screen=screen; _workArea=work; _dragging=null; _snapZone=SnapNone; _snapStart=0; _contentDrag=null; _pointerWindow?.OnPointerLeave(); _pointerWindow=null;
        _lastTitleClick=null; _transition=null; _lowerDirty=true;
        foreach(Window w in _windows)
        {
            w.BackdropStamp=-1; w.ChromeMaterial.Dispose();
            var max=w.MaximumSize; var min=w.MinimumSize;
            Rect b=w.Bounds;
            int width=w.IsFullscreen?Math.Min(screen.W,max.Width):w.IsMaximized?Math.Min(work.W,max.Width):w.CanResize?Math.Max(min.Width,Math.Min(b.W,work.W)):b.W;
            int height=w.IsFullscreen?Math.Min(screen.H,max.Height==int.MaxValue?screen.H:max.Height-Window.TitleBarHeight):w.IsMaximized?Math.Min(work.H,max.Height):w.CanResize?Math.Max(min.Height,Math.Min(b.H,work.H)):b.H;
            int x=Math.Clamp(b.X,0,Math.Max(0,work.W-width)),y=Math.Clamp(b.Y,0,Math.Max(0,work.H-height));
            w.SetBounds(new Rect(w.IsFullscreen||w.IsMaximized?0:x,w.IsFullscreen||w.IsMaximized?0:y,width,height));
            Rect restore=w.RestoreBounds;
            w.RestoreBounds=new Rect(Math.Clamp(restore.X,0,Math.Max(0,work.W-restore.W)),Math.Clamp(restore.Y,0,Math.Max(0,work.H-restore.H)),restore.W,restore.H);
            Rect windowed=w.WindowedBounds;
            w.WindowedBounds=new Rect(Math.Clamp(windowed.X,0,Math.Max(0,work.W-windowed.W)),Math.Clamp(windowed.Y,0,Math.Max(0,work.H-windowed.H)),windowed.W,windowed.H);
        }
    }

    public bool HasTransition => _transition is not null;
    /// <summary>The screen minus the taskbar: where windows open and maximize.</summary>
    public Rect WorkArea => _workArea;

    /// <summary>Open windows, bottom to top.</summary>
    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>Open windows, oldest first; unlike the stack, focus does not reorder it.</summary>
    public IReadOnlyList<Window> OpenOrder => _opened;

    public bool IsDragging => _dragging is not null || _contentDrag is not null;
    /// <summary>A snap preview is on screen (the desktop composes everything while it shows).</summary>
    public bool SnapPreviewShown => _snapZone != SnapNone && _dragging is not null;
    /// <summary>The snap preview is still growing (the desktop keeps drawing).</summary>
    public bool SnapMoving => SnapPreviewShown && _snapStart != 0;
    public int SnapZone => _snapZone;

    internal static int ZoneAt(Rect work, int x, int y) => WindowSnap.ZoneAt(work, x, y);
    internal static Rect SnapTarget(Rect work, int zone) => WindowSnap.Target(work, zone);
    private static bool Fits(Window w, Rect target)
    {
        var (minW, minH) = w.MinimumSize; var (maxW, maxH) = w.MaximumSize;
        return target.W >= minW && target.H >= minH && target.W <= maxW && (maxH == int.MaxValue || target.H <= maxH);
    }
    /// <summary>The preview's rectangle now: grows from the pointer to the target, eased.</summary>
    private Rect SnapPreview()
    {
        Rect target = SnapTarget(_workArea, _snapZone);
        if (_snapStart == 0) { return target; }
        long duration = SnapMicros * AnimationPercent() / 100, elapsed = Micros - _snapStart;
        if (duration <= 0 || elapsed >= duration) { _snapStart = 0; return target; }
        int k = Zenith.Gui.Motion.MotionCurve.EaseOut(Zenith.Gui.Motion.MotionCurve.Linear(elapsed, duration)), one = Zenith.Gui.Motion.MotionCurve.One;
        int Mix(int a, int b) => a + (int)((long)(b - a) * k / one);
        return new Rect(Mix(_snapFrom.X, target.X), Mix(_snapFrom.Y, target.Y), Mix(_snapFrom.W, target.W), Mix(_snapFrom.H, target.H));
    }
    private void TrackSnap(Window w, int x, int y)
    {
        int zone = w.CanResize && !w.IsFullscreen ? ZoneAt(_workArea, x, y) : SnapNone;
        if (zone != SnapNone && !Fits(w, SnapTarget(_workArea, zone))) { zone = SnapNone; }
        if (zone == _snapZone) { return; }
        // A new zone grows from where the old preview was, or from a small square at the pointer.
        _snapFrom = _snapZone != SnapNone ? SnapPreview() : new Rect(x - 24, y - 24, 48, 48);
        _snapZone = zone;
        _snapStart = zone != SnapNone && AnimationPercent() > 0 ? Micros : 0;
    }
    /// <summary>Releasing a title drag over a zone snaps the window there.</summary>
    private void FinishSnap(Window w)
    {
        int zone = _snapZone; _snapZone = SnapNone; _snapStart = 0;
        if (zone == SnapNone || w.IsClosed) { return; }
        Rect from = w.Bounds, before = w.SnapRestore ?? _dragStartBounds;
        if (zone == SnapTop)
        {
            if (w.IsMaximized) { return; }
            w.SnapRestore = null; w.SetBounds(new Rect(from.X, from.Y, before.W, before.H));
            ToggleMaximize(w);
            Zenith.Core.Log.Write("windows", "snapped top " + w.Title);
            return;
        }
        w.SnapRestore = before;
        w.SetBounds(SnapTarget(_workArea, zone));
        if (w == Top) { _transition = new(WindowChange.Resized, w, from); }
        Zenith.Core.Log.Write("windows", "snapped " + WindowSnap.Names[zone] + " " + w.Title);
    }

    public Window? Focused => _windows.Count > 0 && _windows[^1].IsFocused ? _windows[^1] : null;

    public void Open(Window window)
    {
        // Center, then step each new window down-right so they do not stack exactly.
        int x = _workArea.X + (_workArea.W - window.Bounds.W) / 2 + _cascade;
        int y = _workArea.Y + (_workArea.H - window.Bounds.H) / 2 + _cascade;
        _cascade = (_cascade + 28) % 140;
        window.SetBounds(new Rect(Math.Clamp(x, 0, Math.Max(0, _workArea.W-window.Bounds.W)), Math.Clamp(y, 0, Math.Max(0, _workArea.H-window.Bounds.H)), window.Bounds.W, window.Bounds.H));

        _windows.Add(window);
        _opened.Add(window);
        Focus(window);
        _transition = new(WindowChange.Opened, window, window.Bounds);
        Rect b = window.Bounds;
        Zenith.Core.Log.Write("windows", "opened at " + b.X + "," + b.Y + " " + b.W + "x" + b.H);
    }

    /// <summary>The latest change since the last call. State has already changed; only the top window's changes are reported.</summary>
    public WindowTransition? TakeTransition()
    {
        WindowTransition? transition = _transition;
        _transition = null;
        return transition;
    }

    public void Focus(Window window)
    {
        if (window.IsMinimized) { _transition = new(WindowChange.Restored, window, window.Bounds); }
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
        if (window == Top) { _transition = new(WindowChange.Minimized, window, window.Bounds); }
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

        Rect from = window.Bounds;
        if (window.IsMaximized)
        {
            window.IsMaximized = false;
            window.SetBounds(window.RestoreBounds);
        }
        else
        {
            var minimum = window.MinimumSize;
            if (_workArea.W < minimum.Width || _workArea.H < minimum.Height) { return; }
            window.RestoreBounds = window.Bounds;
            window.IsMaximized = true;
            var maximum = window.MaximumSize;
            window.SetBounds(new(_workArea.X, _workArea.Y, Math.Min(_workArea.W, maximum.Width), Math.Min(_workArea.H, maximum.Height)));
        }

        if (window == Top) { _transition = new(WindowChange.Resized, window, from); }
    }

    /// <summary>Toggles the whole screen for a resizable window; its restore state is kept for coming back.</summary>
    public void ToggleFullscreen(Window window)
    {
        if (!window.CanResize) { return; }
        Rect from = window.Bounds;
        if (window.IsFullscreen)
        {
            window.IsFullscreen = false;
            window.SetBounds(window.WindowedBounds);
        }
        else
        {
            var minimum = window.MinimumSize;
            if (_screen.W < minimum.Width || _screen.H < minimum.Height - Window.TitleBarHeight) { return; }
            window.WindowedBounds = window.Bounds;
            window.IsFullscreen = true;
            var maximum = window.MaximumSize;
            int height = maximum.Height == int.MaxValue ? _screen.H : Math.Min(_screen.H, maximum.Height - Window.TitleBarHeight);
            window.SetBounds(new(_screen.X, _screen.Y, Math.Min(_screen.W, maximum.Width), height));
        }

        if (window == Top) { _transition = new(WindowChange.Resized, window, from); }
    }

    /// <summary>Removes windows that closed themselves; returns true if any window changed.</summary>
    private int _barHeight = -1;
    public bool Update()
    {
        bool changed = false, closedOne = false;
        // A new decoration changes the bar height: every window keeps its content size, so programs are not resized.
        int bar = Window.TitleBarHeight;
        if (_barHeight >= 0 && bar != _barHeight)
        {
            int delta = bar - _barHeight;
            foreach (Window w in _windows)
            {
                if (w.IsFullscreen) { continue; }
                if (w.IsMaximized) { w.SetBounds(w.Bounds); continue; }
                w.SetBounds(new Rect(w.Bounds.X, w.Bounds.Y, w.Bounds.W, w.Bounds.H + delta));
                w.ContentVersion++;
            }
            Zenith.Core.Log.Write("windows", "decoration " + Decorations.Current.ToString().ToLowerInvariant() + " bar=" + bar);
            changed = true;
        }
        _barHeight = bar;
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window w = _windows[i];
            using var identity = Zenith.Core.Security.CredentialContext.Enter(w.Identity);
            // Update may close the window. It leaves now: until the next update it would still be on top and take keys.
            if (!w.IsClosed && (!w.IsMinimized || w.UpdateWhenMinimized) && w.Update())
            {
                changed = true;
                w.ContentVersion++;
                w.ContentDamage = w.ContentDamage.Union(w.UpdateDamage is Rect part && !part.IsEmpty
                    ? part.Offset(w.Content.X, w.Content.Y).Intersect(w.Content) : AreaOf(w));
                if (!w.IsMinimized && w != Top) { _lowerDirty = true; w.ContentChanged = true; }
            }

            if (w.IsClosed)
            {
                if (_dragging == w) { _dragging = null; }
                if (_contentDrag == w) { _contentDrag = null; }
                if (_pointerWindow == w) { _pointerWindow = null; }
                if (_lastTitleClick == w) { _lastTitleClick = null; }
                if (w == Top && !closedOne) { _transition = new(WindowChange.Closed, w, w.Bounds); }
                closedOne = true;
                _windows.RemoveAt(i);
                _opened.Remove(w);
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
                Window released = _dragging;
                _dragging = null;
                if (_edges == Edges.None) { FinishSnap(released); }
            }

            RouteKeys(input);
            return true;
        }

        if (_contentDrag is not null)
        {
            Window target = _contentDrag;
            if (target != Top) { _lowerDirty = true; }
            using var identity = Zenith.Core.Security.CredentialContext.Enter(target.Identity);
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
        RoutePointerMotion(hit, input);

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
                using var identity = Zenith.Core.Security.CredentialContext.Enter(hit.Identity);
                hit.OnMouseDown(input.X - hit.Content.X, input.Y - hit.Content.Y);
                _contentDrag = hit;
            }
        }

        if (input.RightPressed && !input.Left && hit is not null && hit.Content.Contains(input.X, input.Y))
        {
            Focus(hit);
            using var identity = Zenith.Core.Security.CredentialContext.Enter(hit.Identity);
            hit.OnSecondaryPress(input.X - hit.Content.X, input.Y - hit.Content.Y);
        }

        if (input.MiddlePressed && !input.Left && hit is not null && hit.Content.Contains(input.X, input.Y))
        {
            Focus(hit);
            using var identity = Zenith.Core.Security.CredentialContext.Enter(hit.Identity);
            hit.OnMiddlePress(input.X - hit.Content.X, input.Y - hit.Content.Y);
        }

        if (input.Scroll != 0 && hit is not null)
        {
            using var identity = Zenith.Core.Security.CredentialContext.Enter(hit.Identity);
            if (hit != Top) { _lowerDirty = true; }
            // Wheel down reports positive deltas; windows take positive as "scroll back".
            hit.OnScrollAt(-input.Scroll, input.X - hit.Content.X, input.Y - hit.Content.Y);
        }

        RouteKeys(input);
        return hit is not null;
    }

    private void RouteKeys(Input input)
    {
        Window? focused = Focused;
        if (focused is null) { return; }
        foreach (var key in input.Keys)
        {
            using var identity = Zenith.Core.Security.CredentialContext.Enter(focused.Identity);
            focused.OnKey(key);
        }
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
        // A title drag is a completed gesture, not the first click of a double
        // click. Otherwise a quick second grab unexpectedly maximizes the app.
        if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4) { _lastTitleClick = null; }

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

            // Pulling a snapped window away gives it back its size, under the pointer like a maximized one.
            if (w.SnapRestore is Rect snapped && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                int grabX = (_dragStartX - w.Bounds.X) * snapped.W / Math.Max(1, w.Bounds.W);
                w.SnapRestore = null;
                w.SetBounds(new Rect(x - grabX, w.Bounds.Y, snapped.W, snapped.H));
                StartDrag(w, Edges.None, x, y);
                return;
            }

            if (!w.IsMaximized)
            {
                MoveTo(w, _dragStartBounds.X + dx, _dragStartBounds.Y + dy);
                TrackSnap(w, x, y);
            }

            return;
        }

        var (minW, minH) = w.MinimumSize;
        Rect b = _dragStartBounds;
        int left = b.X, top = b.Y, right = b.Right, bottom = b.Bottom;
        if ((_edges & Edges.Left) != 0)
        {
            left = Math.Clamp(b.X + dx, Math.Min(_workArea.X, right - minW), right - minW);
        }

        if ((_edges & Edges.Right) != 0)
        {
            right = Math.Clamp(b.Right + dx, left + minW, Math.Max(left + minW, _workArea.Right));
        }

        if ((_edges & Edges.Top) != 0)
        {
            top = Math.Clamp(b.Y + dy, Math.Min(_workArea.Y, bottom - minH), bottom - minH);
        }

        if ((_edges & Edges.Bottom) != 0)
        {
            bottom = Math.Clamp(b.Bottom + dy, top + minH, Math.Max(top + minH, _workArea.Bottom));
        }

        var maximum = w.MaximumSize;
        w.SetBounds(new Rect(left, top, Math.Min(right - left, maximum.Width), Math.Min(bottom - top, maximum.Height)));
    }

    /// <summary>Which edges a press at (x, y) would resize; none inside the title buttons or for fixed windows.</summary>
    /// <summary>The pointer's shape at (x, y): the resize direction on an edge or while resizing,
    /// Move while a window is dragged, a text beam over text content, else the arrow.</summary>
    public Zenith.Gui.Graphics.CursorShape CursorAt(int x, int y)
    {
        if (_dragging is not null) { return _edges == Edges.None ? Zenith.Gui.Graphics.CursorShape.Move : ShapeOf(_edges); }
        if (_contentDrag is { TextContent: true }) { return Zenith.Gui.Graphics.CursorShape.Text; }
        Window? hit = HitTest(x, y, true);
        if (hit is null) { return Zenith.Gui.Graphics.CursorShape.Default; }
        Edges edges = EdgesAt(hit, x, y);
        if (edges != Edges.None) { return ShapeOf(edges); }
        return hit.TextContent && hit.Content.Contains(x, y) ? Zenith.Gui.Graphics.CursorShape.Text : Zenith.Gui.Graphics.CursorShape.Default;
    }

    private static Zenith.Gui.Graphics.CursorShape ShapeOf(Edges edges) => edges switch
    {
        Edges.Left or Edges.Right => Zenith.Gui.Graphics.CursorShape.EwResize,
        Edges.Top or Edges.Bottom => Zenith.Gui.Graphics.CursorShape.NsResize,
        Edges.Left | Edges.Top or Edges.Right | Edges.Bottom => Zenith.Gui.Graphics.CursorShape.NwseResize,
        _ => Zenith.Gui.Graphics.CursorShape.NeswResize,
    };

    private static Edges EdgesAt(Window w, int x, int y)
    {
        if (!w.CanResize || w.IsMaximized || w.IsFullscreen)
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

    /// <summary>The window drawn last, the only one the scene below it does not depend on.</summary>
    public Window? Top
    {
        get
        {
            for (int i = _windows.Count - 1; i >= 0; i--) { if (!_windows[i].IsMinimized) { return _windows[i]; } }
            return null;
        }
    }

    /// <summary>True when drawing the windows below the top one would repeat the last <see cref="DrawLower"/>.</summary>
    public bool LowerUnchanged(int mouseX, int mouseY)
    {
        if (_lowerDirty) { return false; }
        Window? top = Top, hovered = _dragging is null ? HitTest(mouseX, mouseY, includeResizeMargin: false) : null;
        int at = 0;
        foreach (Window w in _windows)
        {
            if (w.IsMinimized || w == top) { continue; }
            if (at == _lower.Count || !_lower[at].Shows(w, w == hovered ? ButtonAt(w, mouseX, mouseY) : 0)) { return false; }
            at++;
        }
        return at == _lower.Count;
    }

    /// <summary>Draws the windows below the top one and remembers what they looked like.
    /// <paramref name="sceneKey"/> changes whenever the wallpaper or appearance does: then every
    /// window blurs its backdrop again. Otherwise only windows over a changed area do.</summary>
    public void DrawLower(Surface surface, int mouseX, int mouseY, long sceneKey)
    {
        Window? top = Top, hovered = _dragging is null ? HitTest(mouseX, mouseY, includeResizeMargin: false) : null;
        _changed.Clear(); _generation++;
        bool sameScene = sceneKey == _sceneKey; _sceneKey = sceneKey;
        // Windows that left the lower scene (closed, minimized, raised) reveal what was under them.
        foreach (LowerWindow old in _lower)
        {
            Window w = old.Window;
            if (w.IsClosed || w.IsMinimized || w == top || !_windows.Contains(w)) { _changed.Add(old.Bounds.Inflate(Theme.ShadowSize + 2)); }
        }
        var previous = new List<LowerWindow>(_lower);
        _lower.Clear(); _lowerDirty = false;
        foreach (Window w in _windows)
        {
            if (w.IsMinimized || w == top) { continue; }
            int button = w == hovered ? ButtonAt(w, mouseX, mouseY) : 0;
            bool same = false;
            foreach (LowerWindow old in previous) { if (old.Shows(w, button)) { same = true; break; } }
            _lower.Add(new LowerWindow(w, button));
            bool known = sameScene && w.BackdropStamp == sceneKey && SameRect(w.BackdropBounds, w.Bounds);
            w.BackdropUnchanged = known && !ChangedUnder(w.Bounds);
            w.BackdropDirty = known ? DirtyUnder(w.Bounds, _changed) : new Rect(0, 0, 0, 0);
            w.BackdropStamp = sceneKey; w.BackdropBounds = w.Bounds; w.BackdropGeneration = _generation;
            DrawWindow(surface, surface, w, w == hovered ? mouseX : -1, mouseY);
            // A window that looks different changes the backdrop of every window above it.
            if (!same || w.ContentChanged || !w.BackdropUnchanged) { _changed.Add(w.Bounds.Inflate(Theme.ShadowSize + 2)); }
            w.ContentChanged = false; w.ContentDamage = new Rect(0, 0, 0, 0); w.BackdropUnchanged = false; w.BackdropDirty = new Rect(0, 0, 0, 0);
        }
    }

    /// <summary>The title-bar button the pointer is over in this window (0 when none or dragging).</summary>
    public int HoverButton(Window w, int x, int y) => _dragging is null && HitTest(x, y, includeResizeMargin: false) == w ? ButtonAt(w, x, y) : 0;

    /// <summary>Marks a window as having drawn new content, as its Update would (benchmark).</summary>
    public void MarkChanged(Window w)
    {
        w.ContentVersion++; w.ContentDamage = AreaOf(w);
        if (!w.IsMinimized && w != Top) { _lowerDirty = true; w.ContentChanged = true; }
    }

    /// <summary>Area a window changes on screen, shadow included.</summary>
    public static Rect AreaOf(Window w) => w.IsFullscreen ? w.Bounds : w.Bounds.Inflate(Theme.ShadowSize + 8);

    /// <summary>The same lower windows in the same order as the cached lower scene: then only what
    /// changed needs drawing again, see <see cref="LowerDamage"/>.</summary>
    public bool LowerStructureUnchanged()
    {
        Window? top = Top; int at = 0;
        foreach (Window w in _windows)
        {
            if (w.IsMinimized || w == top) { continue; }
            if (at == _lower.Count || _lower[at].Window != w) { return false; }
            at++;
        }
        return at == _lower.Count;
    }

    /// <summary>What changed in the lower scene since it was drawn: windows that moved, changed state
    /// (old and new area) or drew new content. Empty when nothing did.</summary>
    public Rect LowerDamage(int mouseX, int mouseY)
    {
        Window? hovered = _dragging is null ? HitTest(mouseX, mouseY, includeResizeMargin: false) : null;
        Rect damage = new(0, 0, 0, 0);
        for (int i = 0; i < _lower.Count; i++)
        {
            Window w = _lower[i].Window;
            if (!_lower[i].Shows(w, w == hovered ? ButtonAt(w, mouseX, mouseY) : 0)) { damage = damage.Union(_lower[i].Bounds.Inflate(Theme.ShadowSize + 8)).Union(AreaOf(w)); }
            else if (w.ContentChanged) { damage = damage.Union(w.ContentDamage.IsEmpty ? AreaOf(w) : w.ContentDamage); }
        }
        return damage;
    }

    /// <summary>Draws the lower scene again inside <paramref name="damage"/> only, over the wallpaper.
    /// A translucent window over changed pixels changes too, but only within its material's reach of
    /// them (<see cref="MaterialComposition.Reach"/>): the damage grows by that much, bottom up. A capture
    /// that changes a window everywhere (new opacity, fresh capture) draws once more over the whole window.
    /// The caller presents the grown rectangle.</summary>
    public void DrawLowerPartial(Surface surface, Surface wallpaper, ref Rect damage, int mouseX, int mouseY, long sceneKey)
    {
        Window? hovered = _dragging is null ? HitTest(mouseX, mouseY, includeResizeMargin: false) : null;
        bool sameScene = sceneKey == _sceneKey;
        _looks.Clear(); _was.Clear();
        for (int i = 0; i < _lower.Count; i++)
        {
            Window w = _lower[i].Window;
            _looks.Add(_lower[i].Shows(w, w == hovered ? ButtonAt(w, mouseX, mouseY) : 0));
            _was.Add(_lower[i].Bounds.Inflate(Theme.ShadowSize + 8));
        }
        damage = GrowForMaterials(damage.Intersect(surface.Bounds), sameScene).Intersect(surface.Bounds);
        _generation++; _sceneKey = sceneKey;
        for (int pass = 0; ; pass++)
        {
            surface.CopyRect(wallpaper, damage);
            surface.BaseClip = damage; MaterialComposition.Limit = damage;
            // What really changed under each window, bottom up: windows that moved or changed state (old
            // and new area), new content, and the reach of a window whose own backdrop changed.
            _below.Clear();
            if (!sameScene) { _below.Add(damage); }
            Rect whole = new(0, 0, 0, 0);
            for (int i = 0; i < _lower.Count; i++)
            {
                Window w = _lower[i].Window;
                Rect area = AreaOf(w), hit = area.Intersect(damage);
                if (hit.W <= 0 || hit.H <= 0) { continue; }
                bool known = w.BackdropStamp == sceneKey && SameRect(w.BackdropBounds, w.Bounds);
                Rect dirty = known ? DirtyUnder(w.Bounds.Inflate(ChangeMargin), _below) : w.Bounds;
                w.BackdropUnchanged = known && dirty.IsEmpty;
                w.BackdropDirty = known ? dirty : new Rect(0, 0, 0, 0);
                w.BackdropStamp = sceneKey; w.BackdropBounds = w.Bounds; w.BackdropGeneration = _generation;
                DrawWindow(surface, surface, w, w == hovered ? mouseX : -1, mouseY);
                bool wholly = !w.BackdropUnchanged && w.MaterialWhollyChanged;
                if (!_looks[i]) { _below.Add(_was[i]); _below.Add(area); }
                else
                {
                    if (w.ContentChanged) { _below.Add(w.ContentDamage.IsEmpty ? area : w.ContentDamage); }
                    if (!dirty.IsEmpty) { _below.Add(wholly ? area : dirty.Inflate(MaterialComposition.Reach).Intersect(area)); }
                }
                if (wholly && !Covers(damage, area.Intersect(surface.Bounds))) { whole = whole.Union(area); }
                w.BackdropUnchanged = false; w.BackdropDirty = new Rect(0, 0, 0, 0);
            }
            if (whole.IsEmpty || pass == 1) { break; }
            damage = GrowForMaterials(damage.Union(whole), sameScene).Intersect(surface.Bounds);
        }
        for (int i = 0; i < _lower.Count; i++)
        {
            Window w = _lower[i].Window;
            _lower[i] = new LowerWindow(w, w == hovered ? ButtonAt(w, mouseX, mouseY) : 0);
            w.ContentChanged = false; w.ContentDamage = new Rect(0, 0, 0, 0);
        }
        _changed.Clear();
        foreach (Rect c in _below) { Rect hit = c.Intersect(damage); if (!hit.IsEmpty) { _changed.Add(hit); } }
        surface.BaseClip = surface.Bounds; MaterialComposition.Limit = MaterialComposition.Unlimited;
        _lowerDirty = false;
    }

    private readonly List<bool> _looks = new();
    private readonly List<Rect> _was = new();
    private static bool Covers(Rect outer, Rect inner) => inner.IsEmpty || (inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom);

    /// <summary>The damage grown, bottom up, over what each translucent lower window can change: its
    /// material's reach around everything that changed below it.</summary>
    private Rect GrowForMaterials(Rect damage, bool sameScene)
    {
        if (MaterialResources.Policy.Effective == 0) { return damage; }
        Rect changed = sameScene ? new Rect(0, 0, 0, 0) : damage;
        for (int i = 0; i < _lower.Count; i++)
        {
            Window w = _lower[i].Window;
            Rect area = AreaOf(w), under = changed.Intersect(area);
            if (!under.IsEmpty)
            {
                Rect grown = under.Inflate(MaterialComposition.Reach).Intersect(area);
                damage = damage.Union(grown); changed = changed.Union(grown);
            }
            if (!_looks[i]) { changed = changed.Union(_was[i]).Union(area); }
            else if (w.ContentChanged) { changed = changed.Union(w.ContentDamage.IsEmpty ? area : w.ContentDamage); }
        }
        return damage;
    }

    /// <summary>Grows a partial compose's damage over the top window by its material's reach, where the
    /// lower scene changed under it.</summary>
    public Rect GrowForTop(Rect damage, Rect lowerChange)
    {
        if (Top is not Window top || MaterialResources.Policy.Effective == 0) { return damage; }
        Rect area = AreaOf(top), under = lowerChange.Intersect(area);
        return under.IsEmpty ? damage : damage.Union(under.Inflate(MaterialComposition.Reach).Intersect(area));
    }

    /// <summary>The top window's last capture changed it everywhere (see <see cref="MaterialComposition.WholeChanged"/>).</summary>
    public bool TopWhollyChanged => Top is Window top && top.MaterialWhollyChanged;

    /// <summary>Draws the top window; <paramref name="stamp"/> names the lower scene already on the surface.</summary>
    public void DrawTop(Surface surface, Surface backdrop, int mouseX, int mouseY, long sceneKey)
    {
        Window? top = Top;
        if (top is null) { return; }
        // Same scene and place, and either no lower redraw since, or one that changed nothing under it.
        bool known = top.BackdropStamp == sceneKey && sceneKey == _sceneKey && SameRect(top.BackdropBounds, top.Bounds);
        top.BackdropUnchanged = known && (top.BackdropGeneration == _generation || (top.BackdropGeneration == _generation - 1 && !ChangedUnder(top.Bounds)));
        // One lower redraw since the last capture: only what it changed is sampled again.
        top.BackdropDirty = known && top.BackdropGeneration == _generation - 1 ? DirtyUnder(top.Bounds, _changed) : new Rect(0, 0, 0, 0);
        top.BackdropStamp = sceneKey; top.BackdropBounds = top.Bounds; top.BackdropGeneration = _generation;
        bool hovered = _dragging is null && HitTest(mouseX, mouseY, includeResizeMargin: false) == top;
        if (SnapPreviewShown && _dragging == top)
        {
            Rect preview = SnapPreview();
            int radius = _snapZone == SnapTop ? 0 : Decorations.Radius(Decorations.Current);
            surface.FillRoundRect(preview.Inflate(-6), radius, (Theme.Accent & 0x00FFFFFFu) | 0x40000000u);
            surface.StrokeRoundRect(preview.Inflate(-6), radius, (Theme.Accent & 0x00FFFFFFu) | 0xB0000000u);
        }
        DrawWindow(surface, backdrop, top, hovered ? mouseX : -1, mouseY);
        top.BackdropUnchanged = false; top.BackdropDirty = new Rect(0, 0, 0, 0); top.ContentDamage = new Rect(0, 0, 0, 0);
    }

    /// <summary>Forgets the lower scene, for the benchmark and for pixel comparison.</summary>
    public void InvalidateLower() => _lowerDirty = true;

    /// <summary>Moves the top window, for the benchmark.</summary>
    public void NudgeTop(int dx)
    {
        if (Top is Window top) { top.SetBounds(top.Bounds.Offset(dx, 0)); }
    }

    private static int ButtonAt(Window w, int x, int y) => w.CloseButton.Contains(x, y) ? 1 : w.MaximizeButton.Contains(x, y) ? 2 : w.MinimizeButton.Contains(x, y) ? 3 : 0;

    /// <param name="backdrop">Where material captures read what is behind the window: the surface itself for
    /// lower windows, the cached lower scene for the top one (the scene may still hold its last frame).</param>
    private static void DrawWindow(Surface surface, Surface backdrop, Window w, int mouseX, int mouseY)
    {
        if (w.IsFullscreen) { DrawFullscreen(surface, backdrop, w); return; }
        Rect b = w.Bounds;
        var style = Decorations.Current;
        int radius = w.IsMaximized ? 0 : Decorations.Radius(style);
        Rect chrome = new(b.X, b.Y, b.W, Window.TitleBarHeight + Theme.WindowRadius);
        long phase = FrameStats.Now;
        MaterialComposition.Dirty = w.BackdropDirty;
        w.ChromeMaterial.Capture(backdrop, chrome, 0, Theme.Panel, w.AllowMaterials, w.BackdropUnchanged);
        w.CaptureBackdrop(backdrop, w.Content);
        MaterialComposition.Dirty = new Rect(0, 0, 0, 0);
        FrameStats.BackdropTicks += FrameStats.Now - phase; phase = FrameStats.Now;
        if (!w.IsMaximized)
        {
            surface.DrawShadow(b, radius, Theme.ShadowSize, w.IsFocused ? Theme.ShadowAlpha : Theme.ShadowAlpha / 2, 6, covered: true);
        }

        // Compose the completed rectangular client only once against the real
        // backdrop. A rectangular clip cannot protect rounded bottom corners.
        surface.CaptureWindowCorners(b, radius);
        surface.FillRect(b, Theme.WindowBackground);

        // Title bar: title on the left, minimize / maximize / close on the right.
        Rect title = w.TitleBar;
        if (Decorations.BarFill(style)) { w.ChromeMaterial.Paint(surface, chrome, Theme.Panel); }
        else { surface.FillRect(title, Theme.WindowBackground); }
        if (Decorations.BarFill(style) && !w.UnifiedChrome && ((MaterialResources.Policy.Effective & 1) == 0 || !w.AllowMaterials))
        { surface.FillRect(new Rect(b.X, title.Bottom - 1, b.W, 1), Theme.TitleDivider); }
        // The chosen decoration's title and controls (the clip may be cut down to a partial compose's damage).
        Decorations.DrawBar(surface, style, b, w.Title, w.IsFocused, w.CanResize, w.IsMaximized, mouseX, mouseY);

        if (!w.UnifiedChrome) { surface.CaptureContentCorner(w.Content, Theme.WindowRadius); }
        surface.Clip = w.Content.Inflate(-1);
        FrameStats.ChromeTicks += FrameStats.Now - phase; phase = FrameStats.Now;
        using var identity = Zenith.Core.Security.CredentialContext.Enter(w.Identity);
        w.DrawContent(surface, w.Content);
        surface.ResetClip();
        FrameStats.ContentTicks += FrameStats.Now - phase; phase = FrameStats.Now;
        if (!w.UnifiedChrome) { surface.CompositeContentCorner(w.Content, Theme.WindowRadius); }

        if (!w.IsMaximized)
        {
            surface.StrokeRoundRect(b, radius, Decorations.Border(style, w.IsFocused));
            surface.CompositeWindowCorners(b, radius);
        }
        FrameStats.ChromeTicks += FrameStats.Now - phase;
    }

    private static void DrawFullscreen(Surface surface, Surface backdrop, Window w)
    {
        long phase = FrameStats.Now;
        MaterialComposition.Dirty = w.BackdropDirty;
        w.CaptureBackdrop(backdrop, w.Content);
        MaterialComposition.Dirty = new Rect(0, 0, 0, 0);
        FrameStats.BackdropTicks += FrameStats.Now - phase; phase = FrameStats.Now;
        surface.FillRect(w.Bounds, Theme.WindowBackground);
        surface.Clip = w.Content;
        using var identity = Zenith.Core.Security.CredentialContext.Enter(w.Identity);
        w.DrawContent(surface, w.Content);
        surface.ResetClip();
        FrameStats.ContentTicks += FrameStats.Now - phase;
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

    private void RoutePointerMotion(Window? hit, Input input)
    {
        Window? over = _contentDrag is null && hit is not null && hit.Content.Contains(input.X, input.Y) ? hit : null;
        if (over != _pointerWindow) { _pointerWindow?.OnPointerLeave(); _pointerWindow = over; }
        else if (!input.Moved) { return; }
        over?.OnPointerMove(input.X - over.Content.X, input.Y - over.Content.Y);
    }

    private Window? HitTest(int x, int y, bool includeResizeMargin)
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window w = _windows[i];
            Rect area = includeResizeMargin && w.CanResize && !w.IsMaximized && !w.IsFullscreen ? w.Bounds.Inflate(ResizeMargin) : w.Bounds;
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
