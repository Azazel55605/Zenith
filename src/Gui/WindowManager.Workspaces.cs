using System;
using System.Collections.Generic;
using Zenith.Core.Configuration;

namespace Zenith.Gui;

internal sealed partial class WindowManager
{
    private DesktopEntry[] _desktops = new DesktopPreferences().Desktops;
    private uint _activeDesktop;
    private readonly List<Window> _recentWindows = new();

    public uint ActiveDesktopId => _activeDesktop;
    public int DesktopCount => _desktops.Length;
    public int ActiveDesktopIndex
    {
        get
        {
            for (int i = 0; i < _desktops.Length; i++) { if (_desktops[i].Id == _activeDesktop) { return i; } }
            return 0;
        }
    }
    public int WorkspaceGeneration { get; private set; }

    public bool IsOnActiveDesktop(Window window) => window.WorkspaceId == _activeDesktop;
    public string DesktopNameAt(int index) => _desktops[index].Name;

    public string DesktopName(uint id)
    {
        foreach (var desktop in _desktops) { if (desktop.Id == id) { return desktop.Name; } }
        return _desktops[0].Name;
    }

    public void InitializeDesktops(DesktopEntry[] desktops)
    {
        _desktops = desktops;
        _activeDesktop = desktops[0].Id;
        WorkspaceGeneration = 0;
        _lowerDirty = true;
    }

    public bool SetDesktops(DesktopEntry[] desktops)
    {
        if (ReferenceEquals(_desktops, desktops)) { return false; }
        _desktops = desktops;
        foreach (Window window in _windows)
        {
            if (!HasDesktop(window.WorkspaceId)) { window.WorkspaceId = desktops[0].Id; }
        }
        if (!HasDesktop(_activeDesktop)) { ChangeWorkspace(desktops[0].Id); }
        else { _lowerDirty = true; }
        return true;
    }

    private bool HasDesktop(uint id)
    {
        foreach (var desktop in _desktops) { if (desktop.Id == id) { return true; } }
        return false;
    }

    public bool SwitchDesktop(int index)
        => index >= 0 && index < _desktops.Length && _desktops[index].Id != _activeDesktop && ChangeWorkspace(_desktops[index].Id);

    public bool StepDesktop(int delta, bool move)
    {
        if (_desktops.Length < 2) { return false; }
        int index = (ActiveDesktopIndex + delta + _desktops.Length) % _desktops.Length;
        Window? window = move ? Focused : null;
        if (move && window is null) { return false; }
        return SwitchToDesktop(index, window);
    }

    private bool SwitchToDesktop(int index, Window? window)
    {
        if (window is not null) { window.WorkspaceId = _desktops[index].Id; }
        ChangeWorkspace(_desktops[index].Id);
        if (window is not null) { Focus(window); }
        return true;
    }

    public void Activate(Window window)
    {
        if (window.IsClosed || !_windows.Contains(window)) { return; }
        if (!IsOnActiveDesktop(window)) { ChangeWorkspace(window.WorkspaceId); }
        Focus(window);
    }

    private bool ChangeWorkspace(uint id)
    {
        if (_pointerWindow is not null) { _pointerWindow.OnPointerLeave(); _pointerWindow = null; }
        if (_contentDrag is not null)
        {
            using var credentials = Zenith.Core.Security.CredentialContext.Enter(_contentDrag.Identity);
            _contentDrag.OnMouseUp(-1, -1);
            _contentDrag = null;
        }
        _dragging = _lastTitleClick = null;
        _snapZone = SnapNone; _snapStart = 0;
        _transition = null;
        CancelPointerFocus();
        foreach (Window window in _windows)
        {
            window.IsFocused = false;
            window.BackdropStamp = -1;
        }
        if (id != _activeDesktop) { WorkspaceGeneration++; }
        _activeDesktop = id;
        _lowerDirty = true;
        foreach (Window window in _recentWindows)
        {
            if (!window.IsClosed && !window.IsMinimized && IsOnActiveDesktop(window))
            { Focus(window); return true; }
        }
        FocusTopmostVisible();
        return true;
    }

    private void RecordFocus(Window window)
    {
        if (_recentWindows.Count > 0 && _recentWindows[0] == window) { return; }
        _recentWindows.Remove(window);
        _recentWindows.Insert(0, window);
    }

    public void SwitcherCandidates(List<Window> result, DesktopPreferences preferences)
    {
        result.Clear();
        IReadOnlyList<Window> order = preferences.SwitcherSort == 1 ? _opened : _recentWindows;
        foreach (Window window in order)
        {
            if (!window.IsClosed && (preferences.SwitcherScope == 1 || IsOnActiveDesktop(window))) { result.Add(window); }
        }
        if (preferences.SwitcherSort == 2)
        { result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)); }
        if (preferences.SwitcherGroup == 0) { return; }
        // Pick the most recently used live window of each application, independently of the displayed order.
        var groups = new Dictionary<string, Window>(StringComparer.OrdinalIgnoreCase);
        foreach (Window window in _recentWindows)
        {
            if (result.Contains(window)) { groups.TryAdd(window.AppId, window); }
        }
        result.RemoveAll(w => groups.TryGetValue(w.AppId, out var winner) && winner != w);
    }
}
