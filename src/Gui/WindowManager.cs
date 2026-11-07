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
        _screen=screen; _workArea=work; _dragging=null; _contentDrag=null; _pointerWindow?.OnPointerLeave(); _pointerWindow=null;
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

    /// <summary>Open windows, bottom to top.</summary>
    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>Open windows, oldest first; unlike the stack, focus does not reorder it.</summary>
    public IReadOnlyList<Window> OpenOrder => _opened;

    public bool IsDragging => _dragging is not null || _contentDrag is not null;

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
    public bool Update()
    {
        bool changed = false, closedOne = false;
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            Window w = _windows[i];
            using var identity = Zenith.Core.Security.CredentialContext.Enter(w.Identity);
            // Update may close the window. It leaves now: until the next update it would still be on top and take keys.
            if (!w.IsClosed && (!w.IsMinimized || w.UpdateWhenMinimized) && w.Update())
            {
                changed = true;
                w.ContentVersion++;
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
                _dragging = null;
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
            w.BackdropUnchanged = sameScene && w.BackdropStamp == sceneKey && SameRect(w.BackdropBounds, w.Bounds) && !ChangedUnder(w.Bounds);
            w.BackdropStamp = sceneKey; w.BackdropBounds = w.Bounds; w.BackdropGeneration = _generation;
            DrawWindow(surface, surface, w, w == hovered ? mouseX : -1, mouseY);
            // A window that looks different changes the backdrop of every window above it.
            if (!same || w.ContentChanged || !w.BackdropUnchanged) { _changed.Add(w.Bounds.Inflate(Theme.ShadowSize + 2)); }
            w.ContentChanged = false; w.BackdropUnchanged = false;
        }
    }

    /// <summary>The title-bar button the pointer is over in this window (0 when none or dragging).</summary>
    public int HoverButton(Window w, int x, int y) => _dragging is null && HitTest(x, y, includeResizeMargin: false) == w ? ButtonAt(w, x, y) : 0;

    /// <summary>Marks a window as having drawn new content, as its Update would (benchmark).</summary>
    public void MarkChanged(Window w)
    {
        w.ContentVersion++;
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
            else if (w.ContentChanged) { damage = damage.Union(AreaOf(w)); }
        }
        return damage;
    }

    /// <summary>Draws the lower scene again inside <paramref name="damage"/> only, over the wallpaper.
    /// A translucent window over the damage is drawn whole (its blur reads everything behind it),
    /// so the damage grows to cover it; the caller presents the grown rectangle.</summary>
    public void DrawLowerPartial(Surface surface, Surface wallpaper, ref Rect damage, int mouseX, int mouseY, long sceneKey)
    {
        Window? top = Top, hovered = _dragging is null ? HitTest(mouseX, mouseY, includeResizeMargin: false) : null;
        if (MaterialResources.Policy.Effective != 0)
        {
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (LowerWindow entry in _lower)
                {
                    Rect area = AreaOf(entry.Window), hit = area.Intersect(damage), joined = damage.Union(area);
                    if (hit.W > 0 && hit.H > 0 && (joined.W != damage.W || joined.H != damage.H || joined.X != damage.X || joined.Y != damage.Y)) { damage = joined; grew = true; }
                }
            }
        }
        damage = damage.Intersect(surface.Bounds);
        _changed.Clear(); _changed.Add(damage); _generation++; _sceneKey = sceneKey;
        surface.CopyRect(wallpaper, damage);
        surface.BaseClip = damage; MaterialComposition.Limit = damage;
        for (int i = 0; i < _lower.Count; i++)
        {
            Window w = _lower[i].Window;
            int button = w == hovered ? ButtonAt(w, mouseX, mouseY) : 0;
            _lower[i] = new LowerWindow(w, button);
            Rect hit = AreaOf(w).Intersect(damage);
            if (hit.W <= 0 || hit.H <= 0) { w.ContentChanged = false; continue; }
            w.BackdropUnchanged = w.BackdropStamp == sceneKey && SameRect(w.BackdropBounds, w.Bounds) && !ChangedUnder(w.Bounds);
            w.BackdropStamp = sceneKey; w.BackdropBounds = w.Bounds; w.BackdropGeneration = _generation;
            DrawWindow(surface, surface, w, w == hovered ? mouseX : -1, mouseY);
            w.ContentChanged = false; w.BackdropUnchanged = false;
        }
        surface.BaseClip = surface.Bounds; MaterialComposition.Limit = MaterialComposition.Unlimited;
        _lowerDirty = false;
    }

    /// <summary>Draws the top window; <paramref name="stamp"/> names the lower scene already on the surface.</summary>
    public void DrawTop(Surface surface, Surface backdrop, int mouseX, int mouseY, long sceneKey)
    {
        Window? top = Top;
        if (top is null) { return; }
        // Same scene and place, and either no lower redraw since, or one that changed nothing under it.
        top.BackdropUnchanged = top.BackdropStamp == sceneKey && sceneKey == _sceneKey && SameRect(top.BackdropBounds, top.Bounds)
            && (top.BackdropGeneration == _generation || (top.BackdropGeneration == _generation - 1 && !ChangedUnder(top.Bounds)));
        top.BackdropStamp = sceneKey; top.BackdropBounds = top.Bounds; top.BackdropGeneration = _generation;
        bool hovered = _dragging is null && HitTest(mouseX, mouseY, includeResizeMargin: false) == top;
        DrawWindow(surface, backdrop, top, hovered ? mouseX : -1, mouseY);
        top.BackdropUnchanged = false;
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
        int radius = w.IsMaximized ? 0 : Theme.WindowRadius;
        Rect chrome = new(b.X, b.Y, b.W, Window.TitleBarHeight + Theme.WindowRadius);
        long phase = FrameStats.Now;
        w.ChromeMaterial.Capture(backdrop, chrome, 0, Theme.Panel, w.AllowMaterials, w.BackdropUnchanged);
        w.CaptureBackdrop(backdrop, w.Content);
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
        w.ChromeMaterial.Paint(surface, chrome, Theme.Panel);
        if (!w.UnifiedChrome && ((MaterialResources.Policy.Effective & 1) == 0 || !w.AllowMaterials))
        { surface.FillRect(new Rect(b.X, title.Bottom - 1, b.W, 1), Theme.TitleDivider); }
        // Lay out in the title box itself: the clip may be cut down to a partial compose's damage.
        Rect titleBox = new(title.X + 14, title.Y, w.MinimizeButton.X - title.X - 20, title.H);
        surface.Clip = titleBox;
        surface.DrawTextCentered(w.Title, Fonts.SemiBold, Theme.TextTitle,
            w.IsFocused ? Theme.TextPrimary : Theme.TextSecondary, titleBox, false);
        surface.ResetClip();

        DrawTitleButton(surface, w.MinimizeButton, TitleIcon.Minimize, mouseX, mouseY, Theme.Hover, w.IsFocused);
        if (w.CanResize)
        {
            DrawTitleButton(surface, w.MaximizeButton, w.IsMaximized ? TitleIcon.Restore : TitleIcon.Maximize, mouseX, mouseY, Theme.Hover, w.IsFocused);
        }

        DrawTitleButton(surface, w.CloseButton, TitleIcon.Close, mouseX, mouseY, Theme.Danger, w.IsFocused);

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
            surface.StrokeRoundRect(b, radius, w.IsFocused ? Theme.WindowBorderFocused : Theme.WindowBorder);
            surface.CompositeWindowCorners(b, radius);
        }
        FrameStats.ChromeTicks += FrameStats.Now - phase;
    }

    private static void DrawFullscreen(Surface surface, Surface backdrop, Window w)
    {
        long phase = FrameStats.Now;
        w.CaptureBackdrop(backdrop, w.Content);
        FrameStats.BackdropTicks += FrameStats.Now - phase; phase = FrameStats.Now;
        surface.FillRect(w.Bounds, Theme.WindowBackground);
        surface.Clip = w.Content;
        using var identity = Zenith.Core.Security.CredentialContext.Enter(w.Identity);
        w.DrawContent(surface, w.Content);
        surface.ResetClip();
        FrameStats.ContentTicks += FrameStats.Now - phase;
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
                for (int i = -4; i <= 4; i++)
                {
                    surface.FillRect(new Rect(cx + i, cy + i, 1, 1), color);
                    surface.FillRect(new Rect(cx + i, cy - i, 1, 1), color);
                }
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
