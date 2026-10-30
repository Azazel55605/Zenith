using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;
using Zenith.Apps;
using Zenith.Core.Shell;
using Zenith.Core.Security;
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
    private NativeAppLauncher _apps = new();
    private NativeClientHost _nativeClients;
    private readonly Taskbar _taskbar;
    private readonly LauncherMenu _launcher;
    private bool _sceneDirty = true;
    private bool _cursorDirty;
    private long _hoverKey;
    private int _cursorX;
    private int _cursorY;
    private int _generation;

    public Desktop(Canvas screen)
    {
        _screen = screen;
        _generation = Accounts.Generation;
        _nativeClients = new NativeClientHost(Accounts.Session);
        Zenith.Core.Processes.NativeProcessGraphics.Register(_nativeClients.ForUser);
        int w = screen.Width, h = screen.Height;
        Zenith.Core.Processes.NativeSystemInformation.Display(w + "x" + h + " @ " + screen.RefreshRate + " Hz");

        _wallpaper = new Surface(w, h);
        _scene = new Surface(w, h);
        _frame = new Surface(w, h);
        Wallpaper.Render(_wallpaper);

        Windows = new WindowManager(new Rect(0, 0, w, h - Taskbar.Height - 2 * Taskbar.Margin));
        _taskbar = new Taskbar(w, h, Windows);
        _launcher = new LauncherMenu(_taskbar.LauncherButton, OpenApp);

        MouseManager.SetScreenSize(w, h);
        MouseManager.SetPosition(w / 2, h / 2);

        TerminalWindow.OpenApp = OpenApp;
        FileManagerWindow.OpenApp = OpenApp;
        CommandShell.Register(new Command("fps", "fps [reset]", "Show compositor statistics", c =>
        {
            if (c.Args.Length == 1 && c.Args[0] == "reset") { FrameStats.ResetWindow(); return 0; }
            if (c.Args.Length != 0) { return c.Fail("usage: fps [reset]"); }
            c.WriteLine("frames presented: " + FrameStats.Frames + ", scene composes: " + FrameStats.SceneComposes);
            c.WriteLine("last compose: " + FrameStats.LastComposeMicros + " us, last present: " + FrameStats.LastPresentMicros + " us");
            c.WriteLine("display: " + _screen.Name + " " + _screen.Width + "x" + _screen.Height);
            c.WriteLine("since reset: " + FrameStats.SceneFrames + " scene frames, average " + FrameStats.AverageFrameMicros + " us, worst " + FrameStats.MaxFrameMicros + " us");
            c.WriteLine("worst compose: " + FrameStats.MaxComposeMicros + " us, worst present: " + FrameStats.MaxPresentMicros + " us");
            c.WriteLine("over 16 ms: " + FrameStats.FramesOver16Ms + ", over 33 ms: " + FrameStats.FramesOver33Ms);
            c.WriteLine("windows: " + Windows.Windows.Count + ", materials: " + MaterialResources.Policy.Effective);
            return 0;
        }));
    }

    /// <summary>Opens a registered app by (case-insensitive) name, passing it an optional argument such as a file path.</summary>
    public bool OpenApp(string name, string? argument)
    {
        foreach (AppInfo app in AppRegistry.Apps)
        {
            if (string.Equals(app.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                if (app.Native is string native && Zenith.Core.Packages.NativePackageSource.State("/usr/share/zenith/native/" + native + ".elf") != Zenith.Core.Packages.PackagePathState.Missing)
                {
                    var args = new System.Collections.Generic.List<string> { native };
                    if (app.Action is not null) { args.Add(app.Action); }
                    if (argument is not null) { args.Add(argument); }
                    string user = Accounts.Service?.Name(CredentialContext.Current.Uid) ?? "user";
                    string cwd = CredentialContext.Current.IsRoot ? "/root" : "/home/" + user;
                    if (!_apps.Launch(native, args.ToArray(), cwd)) { Windows.Open(new AppLaunchNotice("Too many applications are starting. Try again.")); }
                }
                else { Windows.Open(app.Create(argument)); }
                return true;
            }
        }

        return false;
    }

    public WindowManager Windows { get; }

    public void StopClients() { _taskbar.Dispose(); Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(0, 0, 2); _apps.Stop(); _nativeClients.Stop(); Zenith.Core.Processes.NativeSystemInformation.Display("No graphical session"); }

    public void Tick()
    {
        _nativeClients.Tick(Windows);
        _apps.Tick(Windows);
        if (_generation != Accounts.Generation)
        {
            foreach (var window in Windows.Windows) { if (window is TerminalWindow terminal && terminal.IsBusy) { return; } }
            _generation = Accounts.Generation;
            _apps.Stop(); _apps = new NativeAppLauncher();
            _nativeClients.Stop();
            _nativeClients = new NativeClientHost(Accounts.Session);
            Zenith.Core.Processes.NativeProcessGraphics.Register(_nativeClients.ForUser);
            foreach (var window in Windows.Windows) { window.EndSession(); }
            Zenith.Gui.Clipboard.Text = string.Empty;
            Windows.Update();
            using var identity = CredentialContext.Enter(Accounts.Session);
            Windows.Open(new TerminalWindow());
            _sceneDirty = true;
        }
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
            long frameStart = FrameStats.Now;
            ComposeScene();
            _frame.CopyFrom(_scene);
            DrawCursor();
            Present();
            FrameStats.RecordSceneFrame(frameStart);
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
        foreach (KeyEvent key in _input.Keys)
        {
            if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == (ConsoleModifiers.Control | ConsoleModifiers.Alt))
            {
                if (key.Key == ConsoleKeyEx.T) { OpenApp("Terminal", null); return; }
                if (key.Key == ConsoleKeyEx.L) { Zenith.Core.Sessions.SessionRuntime.RequestLogout(); return; }
            }
        }
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
        MaterialResources.Policy.Begin(NativeAppearance.Current.Materials, NativeAppearance.Current.Translucency);
        _scene.CopyFrom(_wallpaper);
        Windows.Draw(_scene, _input.X, _input.Y);

        _taskbar.LauncherOpen = _launcher.IsOpen;
        _taskbar.Draw(_scene, _input.X, _input.Y);
        if (Zenith.Core.Sessions.SessionRuntime.Message is string message)
        {
            _scene.FillRoundRect(new Rect(12, 12, Math.Min(WidthForMessage(), _scene.Width - 24), 40), 6, Theme.Panel);
            _scene.DrawText(message, Fonts.Regular, 13, Theme.TextPrimary, 24, 24);
        }
        if (_launcher.IsOpen)
        {
            _launcher.Draw(_scene, _input.X, _input.Y);
        }

        var policy = MaterialResources.Policy;
        Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(7, policy.Effective, policy.Reason);
        FrameStats.RecordCompose(start);
    }
    private int WidthForMessage() => 520;

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
