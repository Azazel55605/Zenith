using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;
using Zenith.Apps;
using Zenith.Core.Shell;
using Zenith.Gui.Graphics;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Gui.Shell;

/// <summary>
/// The shell root. Each <see cref="Tick"/> polls input, routes it (launcher menu, then taskbar,
/// then windows), and updates the screen with the cheapest path that is correct:
/// <list type="bullet">
/// <item>nothing changed: nothing is drawn;</item>
/// <item>only the pointer moved, over the same control: the pixels under the old cursor are
/// restored from the cached scene and the cursor is drawn at its new position;</item>
/// <item>anything else: the scene (wallpaper, windows, taskbar, menu) is recomposed.</item>
/// </list>
/// Either way the finished frame reaches the display in a single copy.
/// </summary>
internal sealed class Desktop
{
    private readonly Canvas _screen;
    private readonly Surface _wallpaper;
    private readonly Surface _scene;   // everything but the cursor
    private readonly Surface _frame;   // scene + cursor, what is on screen
    private readonly Input _input = new();
    private readonly Taskbar _taskbar;
    private readonly LauncherMenu _launcher;
    private bool _sceneDirty = true;
    private bool _cursorDirty;
    private long _hoverKey;
    private int _cursorX;
    private int _cursorY;

    public Desktop(Canvas screen)
    {
        _screen = screen;
        int w = screen.Width, h = screen.Height;

        _wallpaper = new Surface(w, h);
        _scene = new Surface(w, h);
        _frame = new Surface(w, h);
        Wallpaper.Render(_wallpaper);

        Windows = new WindowManager(new Rect(0, 0, w, h - Taskbar.Height - 2 * Taskbar.Margin));
        _taskbar = new Taskbar(w, h, Windows);
        _launcher = new LauncherMenu(_taskbar.LauncherButton, Windows);

        MouseManager.SetScreenSize(w, h);
        MouseManager.SetPosition(w / 2, h / 2);

        TerminalWindow.OpenApp = OpenApp;
        CommandShell.Register(new Command("fps", "fps", "Show compositor statistics", c =>
        {
            c.WriteLine("frames presented: " + FrameStats.Frames + ", scene composes: " + FrameStats.SceneComposes);
            c.WriteLine("last compose: " + FrameStats.LastComposeMicros + " us, last present: " + FrameStats.LastPresentMicros + " us");
            c.WriteLine("display: " + _screen.Name + " " + _screen.Width + "x" + _screen.Height);
            return 0;
        }));
    }

    /// <summary>Opens a registered app by (case-insensitive) name.</summary>
    public bool OpenApp(string name)
    {
        foreach (AppInfo app in AppRegistry.Apps)
        {
            if (string.Equals(app.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                Windows.Open(app.Create());
                return true;
            }
        }

        return false;
    }

    public WindowManager Windows { get; }

    public void Tick()
    {
        _input.Poll();

        if (_input.LeftPressed || _input.LeftReleased || _input.RightPressed || _input.Scroll != 0 || _input.Keys.Count > 0)
        {
            HandleInput();
            _sceneDirty = true;
        }
        else if (_input.Moved)
        {
            HandleInput();   // drags move windows on motion alone
            long hover = HoverKey();
            if (Windows.IsDragging || hover != _hoverKey)
            {
                _sceneDirty = true;
            }
            else
            {
                _cursorDirty = true;
            }

            _hoverKey = hover;
        }

        if (Windows.Update() | _taskbar.Update())
        {
            _sceneDirty = true;
        }

        if (_sceneDirty)
        {
            ComposeScene();
            _frame.CopyFrom(_scene);
            DrawCursor();
            Present();
        }
        else if (_cursorDirty)
        {
            _frame.CopyRect(_scene, Cursor.Bounds(_cursorX, _cursorY));
            DrawCursor();
            Present();
        }

        _sceneDirty = false;
        _cursorDirty = false;
    }

    /// <summary>Identifies the control under the pointer; a change means hover highlights need redrawing.</summary>
    private long HoverKey()
    {
        int x = _input.X, y = _input.Y;
        long key = _launcher.IsOpen ? _launcher.HoverKey(x, y) : 0;
        key = key * 1024 + _taskbar.HoverKey(x, y);
        return key * 4096 + Windows.HoverKey(x, y);
    }

    private void HandleInput()
    {
        bool menuWasOpen = _launcher.IsOpen;
        if (menuWasOpen)
        {
            // While the menu is open it owns the keyboard and the next click.
            foreach (KeyEvent key in _input.Keys)
            {
                if (key.Key == ConsoleKeyEx.Escape)
                {
                    _launcher.IsOpen = false;
                }
            }

            if (!_input.LeftPressed)
            {
                return;
            }

            _launcher.HandleClick(_input.X, _input.Y);
            if (!_taskbar.Bounds.Contains(_input.X, _input.Y))
            {
                return;
            }
        }

        if (_input.LeftPressed && _taskbar.HandleClick(_input.X, _input.Y, out bool launcherClicked))
        {
            if (launcherClicked)
            {
                _launcher.IsOpen = !menuWasOpen;
            }

            return;
        }

        Windows.HandleInput(_input);
    }

    private void ComposeScene()
    {
        long start = FrameStats.Now;
        _scene.CopyFrom(_wallpaper);
        Windows.Draw(_scene, _input.X, _input.Y);

        _taskbar.LauncherOpen = _launcher.IsOpen;
        _taskbar.Draw(_scene, _input.X, _input.Y);
        if (_launcher.IsOpen)
        {
            _launcher.Draw(_scene, _input.X, _input.Y);
        }

        FrameStats.RecordCompose(start);
    }

    private void DrawCursor()
    {
        _cursorX = _input.X;
        _cursorY = _input.Y;
        Cursor.Draw(_frame, _cursorX, _cursorY);
    }

    private void Present()
    {
        long start = FrameStats.Now;
        _screen.DrawArray(_frame.Pixels, 0, 0, _frame.Width, _frame.Height);
        _screen.Display();
        FrameStats.RecordPresent(start);
    }
}
