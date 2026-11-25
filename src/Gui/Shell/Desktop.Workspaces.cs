using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Configuration;

namespace Zenith.Gui.Shell;

internal sealed partial class Desktop
{
    private readonly TaskSwitcher _switcher = new();
    private DesktopPreferences _desktopPreferences = new();
    private int _workspaceGeneration;
    private const int Alt = 3, Shift = 12, Control = 16;
    private enum TileAction { Focus, Swap, Cycle, Split, Float, Desktop, MoveToDesktop }
    private readonly record struct TileKey(TileAction Action, int X = 0, int Y = 0);
    private readonly System.Collections.Generic.List<TileKey> _tilingKeys = new();
    private int _desktopModifiers, _switcherTabs, _switcherStep, _firstSwitcherStep;

    private void UpdateWorkspacePreferences()
    {
        var preferences = SystemConfiguration.Settings.DesktopPreferences;
        if (ReferenceEquals(_desktopPreferences, preferences)) { return; }
        var previous = _desktopPreferences;
        _desktopPreferences = preferences;
        // Preserve a keyboard-adjusted split when an unrelated desktop preference changes.
        int split = preferences.MasterPercent != previous.MasterPercent ? preferences.MasterPercent : Windows.TileSplit;
        if (Windows.SetTileLayout(split, preferences.TileInnerGap, preferences.TileOuterGap))
        {
            _sceneDirty = _composeAll = true;
        }
        Windows.SetBehavior(new WindowBehavior(preferences.FocusMode, preferences.FocusDelay, preferences.RaiseOnClick,
            preferences.RaiseOnFocus, preferences.TitleDoubleClick, preferences.MaximizeButtonAction,
            preferences.ScreenSnap, preferences.WindowSnapDistance));
        if (Windows.SetDesktops(preferences.Desktops)) { _sceneDirty = _composeAll = _barChanged = true; }
        if (previous.SwitcherLayout != preferences.SwitcherLayout || previous.SwitcherSort != preferences.SwitcherSort
            || previous.SwitcherGroup != preferences.SwitcherGroup || previous.SwitcherScope != preferences.SwitcherScope)
        { CancelSwitcher(); }
    }

    private void CancelSwitcher()
    {
        if (!_switcher.Open) { return; }
        _switcher.Cancel();
        _sceneDirty = _composeAll = true;
    }

    // The input adapters give dedicated arrows the same physical codes on PS/2 and VirtIO.
    private static TileKey? TilingShortcut(int scan, bool control, bool shift)
    {
        if (control)
        {
            if (scan == 0x21 && !shift) { return new(TileAction.Float); }
            return scan is >= 0x02 and <= 0x09
                ? new TileKey(shift ? TileAction.MoveToDesktop : TileAction.Desktop, scan - 0x02) : null;
        }
        TileAction direction = shift ? TileAction.Swap : TileAction.Focus;
        return scan switch
        {
            0x68 => new(direction, 0, -1),
            0x6b => new(direction, -1, 0),
            0x6d => new(direction, 1, 0),
            0x70 => new(direction, 0, 1),
            0x69 when !shift => new(TileAction.Cycle, -1),
            0x71 when !shift => new(TileAction.Cycle, 1),
            0x0c or 0x4a when !shift => new(TileAction.Split, -5),
            0x0d or 0x4e when !shift => new(TileAction.Split, 5),
            _ => null,
        };
    }

    // Use ordered physical edges for the chord. Layout translation can drop
    // Alt+Tab or deliver it as a plain Tab, and never reports Alt's release.
    private bool ReadWorkspaceKeys()
    {
        _switcherTabs = _switcherStep = 0;
        _tilingKeys.Clear();
        if (_input.LostKeyEdges) { _desktopModifiers = 0; CancelSwitcher(); return false; }
        bool altReleased = false;
        foreach (ushort edge in _input.KeyEdges)
        {
            int scan = edge & 255;
            bool released = (edge & 256) != 0;
            int bit = scan switch { 0x38 => 1, 0x60 => 2, 0x2a => 4, 0x36 => 8, 0x1d => Control, _ => 0 };
            if (bit != 0)
            {
                if (released) { _desktopModifiers &= ~bit; }
                else { _desktopModifiers |= bit; }
                if ((bit & Alt) != 0) { altReleased = released && (_desktopModifiers & Alt) == 0; }
            }
            else if (!released && Windows.AutoTile && (_desktopModifiers & Alt) == 1)
            {
                bool control = (_desktopModifiers & Control) != 0;
                bool shift = (_desktopModifiers & Shift) != 0;
                if (TilingShortcut(scan, control, shift) is TileKey key) { _tilingKeys.Add(key); }
            }
            if (scan == 0x0f && !released && (_desktopModifiers & (Alt | Control)) is 1 or 2 or 3)
            {
                int step = (_desktopModifiers & Shift) == 0 ? 1 : -1;
                if (_switcherTabs++ == 0) { _firstSwitcherStep = step; }
                _switcherStep += step;
            }
        }
        return altReleased;
    }

    private void CycleSwitcher(int direction)
    {
        if (_switcher.Open) { _switcher.Step(direction); }
        else if (!Windows.IsDragging)
        {
            _taskbar.ClosePopups();
            _switcher.Start(Windows, _desktopPreferences, direction);
        }
    }

    private bool HandleWorkspaceInput(bool altReleased)
    {
        if (_tilingKeys.Count > 0 && !_switcher.Open && _switcherTabs == 0 && !_taskbar.PopupOpen)
        {
            if (Windows.IsDragging) { return true; }
            foreach (TileKey key in _tilingKeys)
            {
                switch (key.Action)
                {
                    case TileAction.Focus:
                    case TileAction.Swap:
                        Windows.DirectionalTile(key.X, key.Y, key.Action == TileAction.Swap);
                        break;
                    case TileAction.Split: Windows.AdjustTileSplit(key.X); break;
                    case TileAction.Cycle: Windows.StepTile(key.X); break;
                    case TileAction.Float: Windows.ToggleFloating(); break;
                    case TileAction.Desktop:
                    case TileAction.MoveToDesktop:
                        if (key.X < Windows.DesktopCount && key.X != Windows.ActiveDesktopIndex)
                        { Windows.StepDesktop(key.X - Windows.ActiveDesktopIndex, key.Action == TileAction.MoveToDesktop); }
                        break;
                }
            }
            _sceneDirty = _composeAll = _barChanged = true;
            return true;
        }
        bool consumed = _switcher.Open || _switcherTabs != 0;
        if (_switcherTabs != 0)
        {
            CycleSwitcher(_firstSwitcherStep);
            _switcher.Step(_switcherStep - _firstSwitcherStep);
        }
        foreach (KeyEvent key in _input.Keys)
        {
            bool alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
            bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
            bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
            if (!_switcher.Open && alt && ctrl && key.Key is ConsoleKeyEx.LeftArrow or ConsoleKeyEx.RightArrow)
            {
                if (Windows.StepDesktop(key.Key == ConsoleKeyEx.LeftArrow ? -1 : 1, shift)) { _taskbar.ClosePopups(); }
                return true;
            }
            if (key.Key == ConsoleKeyEx.Tab && (_switcherTabs != 0 || (alt && !ctrl)))
            {
                if (_switcherTabs == 0) { CycleSwitcher(shift ? -1 : 1); }
                consumed = true;
                continue;
            }
            if (!_switcher.Open) { continue; }
            if (key.Key == ConsoleKeyEx.Escape) { CancelSwitcher(); }
            else if (key.Key == ConsoleKeyEx.Enter) { _switcher.Accept(Windows); }
            else if (key.Key is ConsoleKeyEx.LeftArrow or ConsoleKeyEx.UpArrow) { _switcher.Step(-1); }
            else if (key.Key is ConsoleKeyEx.RightArrow or ConsoleKeyEx.DownArrow) { _switcher.Step(1); }
        }
        if (_switcher.Open)
        {
            if (_input.LeftPressed) { _switcher.Click(Windows, _input.X, _input.Y, _scene.Width, _scene.Height); }
            else if (altReleased) { _switcher.Accept(Windows); }
        }
        if (consumed || _switcher.Open) { _sceneDirty = _composeAll = true; }
        return consumed || _switcher.Open;
    }

    private bool ApplyWorkspaceTransition()
    {
        if (_workspaceGeneration == Windows.WorkspaceGeneration) { return false; }
        _workspaceGeneration = Windows.WorkspaceGeneration;
        _lowerValid = false;
        _sceneDirty = _composeAll = _barChanged = true;
        _motion.Interrupt(); ReportMotion();
        int percent = SystemConfiguration.Settings.AnimationPercent;
        if (_lock.IsLocked || _portal.IsOpen || _sunset.IsOpen || percent == 0 || _desktopPreferences.DesktopMotion == 0 || !MotionMemory()) { return true; }

        var bounds = _scene.Bounds;
        _motionLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, bounds);
        var (shown, docked) = WantedBar();
        SetBar(shown, docked);
        ComposeScene();
        CopyBase();
        _motion.Begin(_motionBase!, _motionLayer, bounds, bounds, 255, 0, Micros(FrameStats.Now), 140_000 * percent / 100);
        _motionName = "Workspace";
        return true;
    }
}
