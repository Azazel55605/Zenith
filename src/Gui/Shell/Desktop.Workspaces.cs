using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Configuration;

namespace Zenith.Gui.Shell;

internal sealed partial class Desktop
{
    private readonly TaskSwitcher _switcher = new();
    private DesktopPreferences _desktopPreferences = new();
    private int _workspaceGeneration;
    private const int Alt = 3, Shift = 12, Control = 16;
    private int _desktopModifiers, _switcherTabs, _switcherStep, _firstSwitcherStep;

    private void UpdateWorkspacePreferences()
    {
        var preferences = SystemConfiguration.Settings.DesktopPreferences;
        if (ReferenceEquals(_desktopPreferences, preferences)) { return; }
        var previous = _desktopPreferences;
        _desktopPreferences = preferences;
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

    // Use ordered physical edges for the chord. Layout translation can drop
    // Alt+Tab or deliver it as a plain Tab, and never reports Alt's release.
    private bool ReadWorkspaceKeys()
    {
        _switcherTabs = _switcherStep = 0;
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
            else if (scan == 0x0f && !released && (_desktopModifiers & (Alt | Control)) is 1 or 2 or 3)
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
