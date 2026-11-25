using System;
using System.Collections.Generic;
using Zenith.Core.Configuration;
using Zenith.Gui.Graphics;

namespace Zenith.Gui;

internal sealed partial class WindowManager
{
    private readonly List<Window> _tileOrder = new();
    private readonly List<Window> _tileWindows = new();
    private readonly List<AutoTileLayout.Limits> _tileLimits = new();
    private readonly List<Rect> _tileSlots = new();
    private int _tileInnerGap, _tileOuterGap;
    public bool AutoTile { get; }
    public bool HasTiledWindows => AutoTile && _tileWindows.Count > 0;
    public int TileSplit { get; private set; } = DesktopPreferences.DefaultTileSplit;

    public bool SetTileLayout(int percent, int innerGap, int outerGap)
    {
        if (!AutoTile) { return false; }
        int next = Math.Clamp(percent, DesktopPreferences.MinimumTileSplit, DesktopPreferences.MaximumTileSplit);
        if (TileSplit == next && _tileInnerGap == innerGap && _tileOuterGap == outerGap) { return false; }
        TileSplit = next;
        _tileInnerGap = innerGap;
        _tileOuterGap = outerGap;
        RefreshAutoTiles();
        return true;
    }

    public bool AdjustTileSplit(int delta)
        => SetTileLayout(TileSplit + delta, _tileInnerGap, _tileOuterGap);

    private bool CanTile(Window window)
    {
        var minimum = window.MinimumSize;
        return window.CanResize && !window.FloatingByChoice && !window.IsMinimized && !window.IsClosed
            && minimum.Width <= _workArea.W && minimum.Height <= _workArea.H;
    }

    private bool Visible(Window w) => IsOnActiveDesktop(w) && !w.IsMinimized && !w.TileHidden;

    public void RefreshAutoTiles()
    {
        if (!AutoTile) { return; }
        _tileWindows.Clear();
        _tileLimits.Clear();
        int selected = -1;
        foreach (Window w in _tileOrder)
        {
            if (!IsOnActiveDesktop(w)) { continue; }
            w.TileHidden = false;
            bool wasTiled = w.IsTiled;
            w.IsTiled = CanTile(w);
            if (!w.IsTiled)
            {
                if (wasTiled && !w.FloatingByChoice && !w.IsClosed && !w.IsMinimized && w.FloatingBounds is Rect previous)
                {
                    // A smaller display or a decoration change can make this client's minimum impossible.
                    // Restore its independent rectangle and keep the title reachable.
                    Rect rect = FloatingRect(w, previous);
                    if (w.IsMaximized) { w.RestoreBounds = rect; }
                    else if (w.IsFullscreen) { w.WindowedBounds = rect; }
                    else { w.SetBounds(rect); }
                    w.FloatingBounds = null;
                }
                continue;
            }
            w.FloatingBounds ??= w.Bounds;
            if (w.IsFocused) { selected = _tileWindows.Count; }
            _tileWindows.Add(w);
            var min = w.MinimumSize;
            var max = w.MaximumSize;
            _tileLimits.Add(new(min.Width, min.Height, max.Width, max.Height));
        }

        int lastStack = 0;
        foreach (Window w in _recentWindows)
        {
            int index = _tileWindows.IndexOf(w);
            if (index < 0) { continue; }
            if (selected < 0) { selected = index; }
            if (index > 0) { lastStack = index; break; }
        }
        AutoTileLayout.Arrange(_workArea, _tileLimits, TileSplit, _tileInnerGap, _tileOuterGap, selected, lastStack, _tileSlots);
        for (int i = 0; i < _tileWindows.Count; i++)
        {
            Window w = _tileWindows[i];
            Rect slot = _tileSlots[i];
            w.TileHidden = slot.IsEmpty;
            if (w.TileHidden) { continue; }
            if (w.IsMaximized) { w.RestoreBounds = slot; }
            else if (w.IsFullscreen) { w.WindowedBounds = slot; }
            else if (!SameRect(w.Bounds, slot)) { w.SetBounds(slot); }
        }
        _lowerDirty = true;
    }

    public bool StepTile(int delta)
    {
        if (!AutoTile || _tileWindows.Count == 0 || delta == 0) { return false; }
        int index = Focused is Window w ? _tileWindows.IndexOf(w) : -1;
        int next = index < 0 ? (delta < 0 ? _tileWindows.Count - 1 : 0)
            : (index + Math.Sign(delta) + _tileWindows.Count) % _tileWindows.Count;
        return SelectTile(index, next, false);
    }

    public bool DirectionalTile(int dx, int dy, bool swap)
    {
        if (!AutoTile || _tileWindows.Count == 0) { return false; }
        int index = Focused is Window w ? _tileWindows.IndexOf(w) : -1;
        if (index < 0) { return !swap && SelectTile(-1, 0, false); }
        return SelectTile(index, AutoTileLayout.Neighbor(_tileSlots, index, dx, dy), swap);
    }

    private bool SelectTile(int index, int next, bool swap)
    {
        if (next < 0 || index == next) { return false; }
        if (!swap) { Focus(_tileWindows[next]); return true; }
        int first = _tileOrder.IndexOf(_tileWindows[index]), second = _tileOrder.IndexOf(_tileWindows[next]);
        (_tileOrder[first], _tileOrder[second]) = (_tileOrder[second], _tileOrder[first]);
        RefreshAutoTiles();
        return true;
    }

    private Rect FloatingRect(Window w, Rect previous)
    {
        var min = w.MinimumSize;
        return KeepBoundsIn(new(previous.X, previous.Y,
            Math.Max(min.Width, Math.Min(previous.W, _workArea.W)),
            Math.Max(min.Height, Math.Min(previous.H, _workArea.H))), _workArea);
    }

    public bool ToggleFloating()
    {
        if (!AutoTile || Focused is not Window window || !window.CanResize) { return false; }
        if (window.IsFullscreen) { ToggleFullscreen(window); }
        if (window.IsMaximized) { ToggleMaximize(window); }
        if (window.IsTiled && window.FloatingBounds is Rect previous)
        {
            window.SetBounds(FloatingRect(window, previous));
        }
        window.FloatingByChoice = !window.FloatingByChoice;
        window.FloatingBounds = null;
        window.SnapRestore = null;
        RefreshAutoTiles();
        Focus(window);
        return true;
    }

    private void RaiseWindow(Window w)
    {
        // A tiled application's ordinary focus change keeps floating tools above its tile.
        if (AutoTile && w.IsTiled && !w.IsFullscreen && !w.IsMaximized)
        {
            _windows.Remove(w);
            int floating = _windows.FindIndex(other => IsOnActiveDesktop(other) && !other.IsTiled && !other.IsMinimized);
            _windows.Insert(floating < 0 ? _windows.Count : floating, w);
        }
        else if (w != _windows[^1])
        {
            _windows.Remove(w);
            _windows.Add(w);
        }
    }
}
