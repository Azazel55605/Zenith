using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;
using Zenith.Apps;
using Zenith.Core.Shell;
using Zenith.Core.Multiprocessing;
using Zenith.Core.Security;
using Zenith.Gui.Graphics;
using Zenith.Gui.Motion;
using Zenith.Gui.Shell.Prism;
using MemoryInfo = Cosmos.Kernel.System.Diagnostics.MemoryInfo;
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
    private Canvas _screen;
    private Surface _wallpaper;
    private Surface _lower;   // wallpaper and every window below the top one
    private Surface _scene;   // everything but the cursor
    private Surface _frame;   // scene + cursor, what is on screen
    private readonly Input _input = new();
    private NativeAppLauncher _apps = new();
    private NativeClientHost _nativeClients;
    private Taskbar _taskbar;
    private PrismView _prism;
    private readonly System.Collections.Generic.List<Window> _prismWindows = new();
    private bool _lowerValid;
    private (int BlurArea, uint Reason) _lowerSpent;
    private uint _lowerMaterials, _lowerTranslucency, _lowerPanel, _lowerBackground, _lowerBorder, _lowerFocused, _lowerText, _lowerAccent;
    private bool _sceneDirty = true;
    private bool _cursorDirty;
    private long _hoverKey;
    private int _cursorX;
    private int _cursorY;
    private int _generation;

    private const long MotionTestMicros = 150_000, OpenMicros = 150_000, CloseMicros = 120_000, MinimizeMicros = 180_000, ResizeMicros = 160_000;
    private const int PillAlpha = 60;
    private const long BarShowMicros = 160_000, BarHideMicros = 120_000, BarLingerMicros = 400_000;
    private const int BarRevealRows = 3;
    private TaskbarMode _barMode = (TaskbarMode)(-1);
    private readonly PixelLayer _barLayer = new();
    private long _barPointerUntil;
    private const ulong MotionReservePages = 4096;
    private readonly MotionPlayer _motion = new();
    private readonly PixelLayer _motionLayer = new();
    private int[]? _motionBase;
    private volatile bool _motionTestRequested;
    private string? _motionName;
    private volatile bool _benchRequested;
    private volatile bool _parallelBenchRequested;
    private volatile bool _parallelBenchFullscreen;
    private volatile bool _cpuPreviewRequested;
    private const int BenchFrames = 30;

    public Desktop(Canvas screen)
    {
        _screen = screen;
        _generation = Accounts.Generation;
        _nativeClients = new NativeClientHost(Accounts.Session);
        Zenith.Core.Processes.NativeProcessGraphics.Register(_nativeClients.ForUser);
        int w = screen.Width, h = screen.Height;
        Zenith.Core.Processes.NativeSystemInformation.Display(w + "x" + h + " @ " + screen.RefreshRate + " Hz");

        _wallpaper = new Surface(w, h);
        _lower = new Surface(w, h);
        _scene = new Surface(w, h);
        _frame = new Surface(w, h);
        Wallpaper.Render(_wallpaper); _wallpaperKey = Wallpaper.Key();

        Windows = new WindowManager(new Rect(0, 0, w, h - Theme.TaskbarHeight - 2 * Theme.TaskbarMargin), new Rect(0, 0, w, h));
        _taskbar = new Taskbar(w, h, Windows);
        _prism = new PrismView(PrismCandidates, PrismActivate, PrismUser);

        SessionDisplay.ConfigurePointer();
        MouseManager.SetPosition(SessionDisplay.Physical.Width / 2, SessionDisplay.Physical.Height / 2);

        TerminalWindow.OpenApp = OpenApp;
        FileManagerWindow.OpenApp = OpenApp;
        CommandShell.Register(new Command("fps", "fps [reset|motion|bench|cpu|parallel-bench|parallel on|off|direct on|off|cursor hardware|software]", "Show compositor statistics", c =>
        {
            if (c.Args.Length == 2 && c.Args[0] == "direct" && (c.Args[1] == "on" || c.Args[1] == "off"))
            {
                GuardedFilesystem.RequireRoot(); FramePresenter.Direct = c.Args[1] == "on"; _sceneDirty = true;
                c.WriteLine(FramePresenter.Status); return 0;
            }
            if (c.Args.Length == 2 && c.Args[0] == "cursor" && (c.Args[1] == "hardware" || c.Args[1] == "software"))
            {
                // The session thread owns the device pointer: the switch takes effect at its next frame.
                HardwareCursor.Enabled = c.Args[1] == "hardware"; _sceneDirty = true;
                c.WriteLine(HardwareCursor.Status); return 0;
            }
            if (c.Args.Length == 2 && c.Args[0] == "parallel" && (c.Args[1] == "on" || c.Args[1] == "off"))
            {
                GuardedFilesystem.RequireRoot(); CpuWorkers.ParallelBlur = c.Args[1] == "on";
                c.WriteLine(CpuWorkers.BlurStatus); return 0;
            }
            if (c.Args.Length == 1 && c.Args[0] == "reset") { FrameStats.ResetWindow(); return 0; }
            if (c.Args.Length == 1 && c.Args[0] == "verify") { _verifyRequested = true; return 0; }
            if (c.Args.Length == 1 && c.Args[0] == "phases") { c.WriteLine("phases: " + FrameStats.TakeComposePhases()); return 0; }
            // The desktop tick owns the windows, so the test runs there and reports to the log.
            if (c.Args.Length == 1 && c.Args[0] == "motion") { _motionTestRequested = true; return 0; }
            if (c.Args.Length == 1 && c.Args[0] == "cpu")
            {
                GuardedFilesystem.RequireRoot(); _cpuPreviewRequested = true; return 0;
            }
            if (c.Args.Length == 1 && c.Args[0] == "bench") { _benchRequested = true; return 0; }
            if (c.Args.Length >= 1 && c.Args[0] == "parallel-bench" && (c.Args.Length == 1 || (c.Args.Length == 2 && c.Args[1] == "fullscreen")))
            { GuardedFilesystem.RequireRoot(); _parallelBenchFullscreen = c.Args.Length == 2; _parallelBenchRequested = true; return 0; }
            if (c.Args.Length != 0) { return c.Fail("usage: fps [reset|motion|bench|cpu|parallel-bench|parallel on|off|direct on|off|cursor hardware|software]"); }
            c.WriteLine("frames presented: " + FrameStats.Frames + ", scene composes: " + FrameStats.SceneComposes);
            c.WriteLine("last compose: " + FrameStats.LastComposeMicros + " us, last present: " + FrameStats.LastPresentMicros + " us");
            c.WriteLine("display: " + _screen.Name + " " + _screen.Width + "x" + _screen.Height);
            c.WriteLine("since reset: " + FrameStats.SceneFrames + " scene frames, average " + FrameStats.AverageFrameMicros + " us, worst " + FrameStats.MaxFrameMicros + " us");
            c.WriteLine("worst compose: " + FrameStats.MaxComposeMicros + " us, worst present: " + FrameStats.MaxPresentMicros + " us");
            c.WriteLine("over 16 ms: " + FrameStats.FramesOver16Ms + ", over 33 ms: " + FrameStats.FramesOver33Ms);
            c.WriteLine(CpuWorkers.BlurStatus);
            c.WriteLine(FramePresenter.Status);
            c.WriteLine(HardwareCursor.Status + "; " + Zenith.Core.Input.AbsolutePointer.Status);
            c.WriteLine("windows: " + Windows.Windows.Count + ", materials: " + MaterialResources.Policy.Effective);
            if (Windows.Top is Window top)
            {
                Rect b = top.Bounds, pill = _taskbar.PillOf(top) ?? new Rect(0, 0, 0, 0);
                c.WriteLine("top window: " + b.X + " " + b.Y + " " + b.W + " " + b.H + " pill: " + pill.X + " " + pill.Y + " " + pill.W + " " + pill.H);
            }
            c.WriteLine("taskbar: " + _barMode + " shown=" + (_taskbar.Shown ? 1 : 0) + " overflow=" + _taskbar.OverflowCount + " " + _taskbar.OverflowButton.X + " " + _taskbar.OverflowButton.Y + " docked=" + (_taskbar.Docked ? 1 : 0));
            c.WriteLine("last motion: " + _motion.Frames + " frames in " + _motion.ElapsedMicros + " us, worst " + _motion.WorstFrameMicros + " us, ended " + _motion.LastEnd);
            return 0;
        }));
    }

    /// <summary>Opens a registered app by (case-insensitive) name, passing it an optional argument such as a file path.</summary>
    public bool OpenApp(string name, string? argument) => OpenAppWith(name, argument is null ? null : new[] { argument });

    /// <summary>Opens an app by name with extra program arguments (Prism opens a Meridian page
    /// with "--page", ID). An installed native program runs; otherwise its built-in window.</summary>
    public bool OpenAppWith(string name, string[]? arguments)
    {
        // Always the built-in terminal: the way back when an installed Polaris does not work.
        if (string.Equals(name, RecoveryTerminal, System.StringComparison.OrdinalIgnoreCase)) { Windows.Open(new TerminalWindow()); return true; }
        if (string.Equals(name, "Polaris", System.StringComparison.OrdinalIgnoreCase)) { name = "Terminal"; }
        if (AppRegistry.Find(name) is not AppInfo app) { return false; }
        if (app.Native is string native && Zenith.Core.Packages.NativePackageSource.State("/usr/share/zenith/native/" + native + ".elf") != Zenith.Core.Packages.PackagePathState.Missing)
        {
            var args = new System.Collections.Generic.List<string> { native };
            if (app.Action is not null) { args.Add(app.Action); }
            if (arguments is not null) { args.AddRange(arguments); }
            string user = Accounts.Service?.Name(CredentialContext.Current.Uid) ?? "user";
            string cwd = CredentialContext.Current.IsRoot ? "/root" : "/home/" + user;
            if (!_apps.Launch(native, args.ToArray(), cwd)) { Windows.Open(new AppLaunchNotice("Too many applications are open. Close one and try again.")); }
            else { _launchingUntil = FrameStats.Now + 4 * System.Diagnostics.Stopwatch.Frequency; _launchingWindows = Windows.Windows.Count; }
        }
        else { Windows.Open(app.Create(arguments is { Length: 1 } ? arguments[0] : null)); }
        return true;
    }

    /// <summary>What Prism searches: installed apps, open windows, Meridian's pages (when Meridian
    /// is installed) and the session and power actions this account may use.</summary>
    private System.Collections.Generic.List<PrismItem> PrismCandidates()
    {
        var list = new System.Collections.Generic.List<PrismItem>();
        foreach (var app in AppRegistry.Visible())
        { list.Add(new PrismItem(PrismKind.App, app.DisplayName, app.Description, app.Icon ?? AppRegistry.IconFor(app.Name), app.Name, Keywords: app.Description)); }
        _prismWindows.Clear();
        foreach (Window window in Windows.Windows)
        {
            if (window.IsClosed) { continue; }
            _prismWindows.Add(window);
            list.Add(new PrismItem(PrismKind.Window, window.Title, window.IsMinimized ? "Minimized window" : "Open window", window.IconName,
                (_prismWindows.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (AppRegistry.Find("Meridian")?.Installed == true) { list.AddRange(PrismSearch.SettingsPages()); }
        list.AddRange(PrismSearch.Actions(CredentialContext.Current.IsRoot && Zenith.Core.PowerControl.BlockedReason is null));
        return list;
    }

    private string PrismUser() => Accounts.Service?.Name(CredentialContext.Current.Uid) ?? (CredentialContext.Current.IsRoot ? "root" : "user");

    private void PrismActivate(PrismItem item)
    {
        // A state change between drawing and choosing must not reach the panic path.
        try
        {
            switch (item.Kind)
            {
                case PrismKind.App: OpenApp(item.Target, null); break;
                case PrismKind.Window:
                    if (int.TryParse(item.Target, out int index) && index >= 0 && index < _prismWindows.Count && !_prismWindows[index].IsClosed)
                    { Windows.Focus(_prismWindows[index]); }
                    break;
                case PrismKind.Setting: OpenAppWith("Meridian", new[] { "--page", item.Target }); break;
                case PrismKind.Calculator: Zenith.Gui.Clipboard.Text = item.Target; break;
                case PrismKind.Command:
                    // The built-in terminal takes typed input; Polaris has no run-a-command option yet.
                    var terminal = new TerminalWindow(); Windows.Open(terminal);
                    foreach (char c in item.Target) { terminal.OnKey(new KeyEvent(c, ConsoleKeyEx.NoName, false, false, false, KeyEvent.KeyEventType.Make)); }
                    terminal.OnKey(new KeyEvent('\n', ConsoleKeyEx.Enter, false, false, false, KeyEvent.KeyEventType.Make));
                    break;
                case PrismKind.Action:
                    if (item.Target == "logout") { Zenith.Core.Sessions.SessionRuntime.RequestLogout(); }
                    else if (item.Target == "restart") { Zenith.Core.PowerControl.Reboot(); }
                    else if (item.Target == "shutdown") { Zenith.Core.PowerControl.PowerOff(); }
                    break;
            }
        }
        catch (System.Exception e) { Zenith.Core.Log.Write("session", "prism action failed: " + e.Message); }
    }

    internal sealed class DisplayFrames
    {
        public readonly Surface Wallpaper, Lower, Scene, Frame;
        public readonly Taskbar Bar;
        public readonly System.Collections.Generic.List<(Window Window,Rect Bounds,Rect Restore,Rect Windowed,bool Maximized,bool Fullscreen)> Geometry=new();
        public DisplayFrames(int width,int height,WindowManager windows,System.Func<string,string?,bool> open)
        {
            Wallpaper=new Surface(width,height); Lower=new Surface(width,height); Scene=new Surface(width,height); Frame=new Surface(width,height);
            Zenith.Gui.Graphics.Wallpaper.Render(Wallpaper); Bar=new Taskbar(width,height,windows);
        }
        public DisplayFrames(Desktop desktop)
        {
            Wallpaper=desktop._wallpaper; Lower=desktop._lower; Scene=desktop._scene; Frame=desktop._frame;
            Bar=desktop._taskbar;
            foreach(Window w in desktop.Windows.Windows) { Geometry.Add((w,w.Bounds,w.RestoreBounds,w.WindowedBounds,w.IsMaximized,w.IsFullscreen)); }
        }
    }
    internal DisplayFrames PrepareDisplay(int width,int height) => new(width,height,Windows,OpenApp);
    internal DisplayFrames CaptureDisplay() => new(this);
    internal void UseDisplay(Canvas screen,DisplayFrames frames)
    {
        _motion.Interrupt(); _motionBase=null; _screen=screen;
        _wallpaper=frames.Wallpaper; _lower=frames.Lower; _scene=frames.Scene; _frame=frames.Frame;
        int w=screen.Width,h=screen.Height;
        Windows.SetDisplayArea(new Rect(0,0,w,h),new Rect(0,0,w,h-Theme.TaskbarHeight-2*Theme.TaskbarMargin));
        foreach(var item in frames.Geometry)
        {
            if(item.Window.IsClosed) { continue; }
            item.Window.IsMaximized=item.Maximized; item.Window.IsFullscreen=item.Fullscreen;
            item.Window.RestoreBounds=item.Restore; item.Window.WindowedBounds=item.Windowed; item.Window.SetBounds(item.Bounds);
        }
        _taskbar=frames.Bar; _prism.Place(_taskbar.Bounds,screen.Width,screen.Height);
        _barMode=(TaskbarMode)(-1); _lowerValid=false; _sceneDirty=true; _cursorDirty=true; _hoverKey=-1;
        Zenith.Core.Processes.NativeSystemInformation.Display(w+"x"+h+" logical, scale="+SessionDisplay.Percent+"%");
    }
    public WindowManager Windows { get; }

    public void StopClients() { _taskbar.Dispose(); Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(0, 0, 2); _apps.Stop(); _nativeClients.Stop(); Zenith.Core.Processes.NativeSystemInformation.Display("No graphical session"); }

    /// <summary>What is on screen, for the crossfade when this session ends.</summary>
    public int[] FramePixels => _frame.Pixels;
    private const string RecoveryTerminal = "recovery-terminal";
    public bool TerminalBusy => _nativeClients.TerminalBusy;

    // While an animation plays: desktop ticks and the longest gap between two, for the motion log.
    private long _lastTickAt, _motionTicks, _motionGap;
    public void Tick()
    {
        long tickAt = FrameStats.Now;
        if (_motionName is not null) { _motionTicks++; _motionGap = Math.Max(_motionGap, tickAt - _lastTickAt); }
        _lastTickAt = tickAt;
        if (SessionDisplay.FadePending)
        {
            _input.Poll();
            WantedBar();
            ComposeScene();
            _frame.CopyFrom(_scene);
            DrawCursor();
            SessionDisplay.FadeTo(_frame.Pixels, _frame.Width, _frame.Height, welcome: true);
            _sceneDirty = false;
        }

        // Between the device pointer and one drawn into frames the whole frame is presented once:
        // the other kind's pointer must not stay on screen.
        bool hardwarePointer = HardwareCursor.Active;
        if (hardwarePointer != _hardwarePointer) { _hardwarePointer = hardwarePointer; _composeAll = true; _sceneDirty = true; }
        // A new wallpaper choice, light/dark mode or accent redraws the wallpaper now, not at reboot.
        long wallpaper = Wallpaper.Key();
        if (wallpaper != _wallpaperKey)
        {
            _wallpaperKey = wallpaper; Wallpaper.Render(_wallpaper);
            _sceneKey++; _lowerValid = false; _composeAll = true; _sceneDirty = true;
        }

        _nativeClients.Tick(Windows);
        _apps.Tick(Windows);
        PublishWindows();
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

        // A release or a hover change only redraws; the click that started an animation must not cut it short.
        bool interrupts = _input.LeftPressed || _input.RightPressed || _input.Scroll != 0 || _input.Keys.Count > 0;
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
                interrupts |= Windows.IsDragging;
            }
            else
            {
                _cursorDirty = true;
            }

            _hoverKey = hover;
        }

        CursorShape shape = PointerShape();
        if (shape != Cursor.Shape) { Cursor.Shape = shape; _cursorDirty = true; }

        // The recompose at the end of an animation catches up on everything else.
        if (interrupts) { _motion.Interrupt(); }
        bool barUpdated = _taskbar.Update();
        _barChanged |= barUpdated;
        if (Windows.Update() | barUpdated)
        {
            _sceneDirty = true;
        }

        if (_cpuPreviewRequested)
        {
            _cpuPreviewRequested = false;
            try
            {
                uint[] pixels = Zenith.Core.Multiprocessing.CpuWorkers.RenderManagedPreview(out int cpus, out long micros);
                Windows.Open(new CpuFramePreview(pixels, cpus, micros));
                Zenith.Core.Log.Write("cpu", "UI owner published complete managed frame; cpus=" + cpus + " elapsed-us=" + micros);
                _sceneDirty = true;
            }
            catch (System.Exception error) { Zenith.Core.Log.Write("cpu", "frame preview refused: " + error.Message); }
        }
        if (_benchRequested) { _benchRequested = false; RunBench(); _sceneDirty = true; }
        if (_verifyRequested) { _verifyRequested = false; VerifyScene(); _sceneDirty = true; }
        if (_parallelBenchRequested) { _parallelBenchRequested = false; RunParallelBench(_parallelBenchFullscreen); _sceneDirty = true; }
        if (_motionTestRequested) { _motionTestRequested = false; StartMotionTest(); }
        StartTransition();
        _framePending = false;
        // Pace frames instead of sleeping between them: the kernel loop does not halt while a
        // frame is due, and a frame is never drawn sooner than the frame interval after the last.
        bool due = FrameStats.Now - _lastPresent >= FrameInterval;
        // Only motion the person is watching (an animation, a drag) keeps the loop awake until the frame
        // is due; other changes (a client's new frame) wait halted for the next interrupt, at most a tick.
        bool watched = _motion.Active || _motionName is not null || Windows.IsDragging;
        if ((watched || _sceneDirty) && !due) { _framePending = watched; return; }
        if (_motion.Active || _motionName is not null)
        {
            _composeAll = true;   // motion frames bypass the scene; the frame after them composes everything
            if (DrawMotionFrame())
            {
                _sceneDirty = false;
                _cursorDirty = false;
                _framePending = true;
                return;
            }
            _sceneDirty = true;
        }

        if (_sceneDirty)
        {
            long frameStart = FrameStats.Now;
            Rect oldCursor = Cursor.Bounds(_cursorX, _cursorY);
            ComposeScene();
            // Only what changed goes to the screen: the top window's old and new area when the
            // scene below it is unchanged, otherwise everything. The old cursor is restored too.
            Rect damage = _composeDamage.Union(oldCursor).Union(Cursor.Bounds(_input.X, _input.Y)).Intersect(_scene.Bounds);
            _frame.CopyRect(_scene, damage);
            DrawCursor();
            Present(damage);
            FrameStats.RecordSceneFrame(frameStart);
            _framePending = Windows.IsDragging;   // a drag usually draws again next tick
        }
        else if (_cursorDirty)
        {
            Rect old = Cursor.Bounds(_cursorX, _cursorY);
            _frame.CopyRect(_scene, old);
            DrawCursor();
            Present(old.Union(Cursor.Bounds(_cursorX, _cursorY)));
        }

        _sceneDirty = false;
        _cursorDirty = false;
    }

    /// <summary>Applies the taskbar choice and works out the bar's shape and visibility for what is on screen now.</summary>
    private (bool Shown, bool Docked) WantedBar()
    {
        int w = _scene.Width, h = _scene.Height;
        var mode = (TaskbarMode)Zenith.Core.Configuration.SystemConfiguration.Settings.Current.Taskbar;
        if (mode != _barMode)
        {
            _barMode = mode;
            Windows.SetWorkArea(new Rect(0, 0, w, h - TaskbarPolicy.Reserved(mode, Theme.TaskbarHeight, Theme.TaskbarMargin)));
            _sceneDirty = true;
        }

        long now = Micros(FrameStats.Now);
        bool near = _input.Y >= h - BarRevealRows || (_taskbar.Shown && _input.Y >= _taskbar.Bounds.Y - Theme.TaskbarMargin);
        if (near && !Windows.IsDragging) { _barPointerUntil = now + BarLingerMicros; }
        Window? top = Windows.Top;
        bool fullscreen = top?.IsFullscreen == true, maximized = top?.IsMaximized == true;
        return (TaskbarPolicy.Shown(mode, fullscreen, maximized, now < _barPointerUntil, (_prism.IsOpen && _prism.Mode == PrismView.Layout.Menu) || _taskbar.OverflowOpen),
                TaskbarPolicy.Docked(mode, fullscreen, maximized));
    }

    private void SetBar(bool shown, bool docked)
    {
        _taskbar.Shown = shown;
        _taskbar.Docked = docked;
        if (_prism.IsOpen) { _prism.Place(_taskbar.Bounds, _scene.Width, _scene.Height); }
    }

    /// <summary>Composes without the bar when the bar is about to animate, since it travels above everything else.</summary>
    private void ComposeBase(bool barMoves, bool shown, bool docked, bool withoutTop)
    {
        if (barMoves) { SetBar(false, docked); }
        ComposeScene(withoutTop);
        if (barMoves) { SetBar(shown, docked); }
    }

    private void BeginBarOverlay(bool wasShown, bool wasDocked, bool shown, bool docked)
    {
        Rect now = _taskbar.BoundsFor(docked), was = _taskbar.BoundsFor(wasDocked);
        int away = Theme.TaskbarHeight + Theme.TaskbarMargin;
        Rect from = wasShown ? was : now.Offset(0, away), to = shown ? now : was.Offset(0, away);
        _motion.SetOverlay(_barLayer, from, to, Taskbar.RadiusFor(wasShown ? wasDocked : docked), Taskbar.RadiusFor(shown ? docked : wasDocked));
    }

    private static string BarMotionName(bool wasShown, bool shown, bool docked)
        => shown != wasShown ? (shown ? "TaskbarShown" : "TaskbarHidden") : docked ? "TaskbarDocked" : "TaskbarFloated";

    /// <summary>Draws and presents one animation frame; false once the animation is over.</summary>
    private bool DrawMotionFrame()
    {
        long start = FrameStats.Now;
        // Only the layers' old and new places and both pointer positions change between motion frames.
        _motion.Restore(_frame.Pixels, _frame.Width, _frame.Height, Cursor.Bounds(_cursorX, _cursorY));
        if (!_motion.Render(_frame.Pixels, _frame.Width, _frame.Height, Micros(start)))
        {
            ReportMotion();
            return false;
        }
        DrawCursor();
        Present(_motion.Damage.Union(Cursor.Bounds(_cursorX, _cursorY)).Intersect(_frame.Bounds));
        _motion.RecordFrame(Micros(FrameStats.Now) - Micros(start));
        return true;
    }

    private void ReportMotion()
    {
        if (_motionName is null) { return; }
        Zenith.Core.Log.Write("motion", _motionName + " frames=" + _motion.Frames + " elapsed=" + _motion.ElapsedMicros + " worst=" + _motion.WorstFrameMicros + " ended=" + _motion.LastEnd
            + " ticks=" + _motionTicks + " longest-gap=" + _motionGap * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
        _motionName = null; _motionTicks = 0; _motionGap = 0;
    }

    private bool MotionMemory()
    {
        ulong needed = (ulong)(_frame.Width * _frame.Height * 2 * sizeof(int) / 4096) + MotionReservePages;
        if (MemoryInfo.FreePages < needed) { return false; }
        _motionBase ??= new int[_frame.Pixels.Length];
        return true;
    }

    private void CopyBase() => System.Array.Copy(_scene.Pixels, _motionBase!, _motionBase!.Length);

    /// <summary>Most of a rectangle, centered: where a window fades in from and out to.</summary>
    private static Rect Shrunk(Rect area) => new(area.X + area.W / 50, area.Y + area.H / 50, area.W - area.W / 25, area.H - area.H / 25);

    /// <summary>Plays the window manager's latest change and any change it makes to the bar, together. Pixels from
    /// before the change are still in the scene, so what left the screen is captured first; the scene below the top
    /// window comes from its cache.</summary>
    private void StartTransition()
    {
        WindowTransition? change = Windows.TakeTransition();
        var (shown, docked) = WantedBar();
        bool wasShown = _taskbar.Shown, wasDocked = _taskbar.Docked;
        bool barMoves = shown != wasShown || (shown && docked != wasDocked);
        if (change is null && !barMoves)
        {
            if (docked != wasDocked) { SetBar(shown, docked); _sceneDirty = true; }
            return;
        }

        _sceneDirty = true;
        if (change is not null) { _motion.Interrupt(); ReportMotion(); }
        int percent = Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent;
        // A bar change alone waits for nothing, but must not cut a window animation short.
        if (percent == 0 || (change is null && _motion.Active) || !MotionMemory())
        {
            SetBar(shown, docked);
            return;
        }

        if (barMoves && wasShown && !shown) { _barLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, _taskbar.Bounds); }
        PixelLayer? layer = null;
        Rect from = default, to = default;
        int fromAlpha = 255, toAlpha = 255;
        long duration = shown ? BarShowMicros : BarHideMicros;
        if (change is WindowTransition t && !PrepareWindowLayer(t, barMoves, shown, docked, ref from, ref to, ref fromAlpha, ref toAlpha, ref duration))
        {
            SetBar(shown, docked);
            return;
        }
        if (change is not null) { layer = _motionLayer; }
        else { ComposeBase(barMoves, shown, docked, false); CopyBase(); }

        if (barMoves && shown)
        {
            SetBar(true, docked);
            ComposeScene();
            _barLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, _taskbar.Bounds);
        }
        SetBar(shown, docked);

        _motion.Begin(_motionBase!, layer, from, to, fromAlpha, toAlpha, Micros(FrameStats.Now), duration * percent / 100);
        if (barMoves) { BeginBarOverlay(wasShown, wasDocked, shown, docked); }
        string? bar = barMoves ? BarMotionName(wasShown, shown, docked) : null;
        _motionName = change is WindowTransition named ? named.Change + (bar is null ? "" : " bar=" + bar) : bar;
    }

    /// <summary>Captures the window's layer and the base under it; false when this change has nothing to show.</summary>
    private bool PrepareWindowLayer(WindowTransition t, bool barMoves, bool shown, bool docked, ref Rect from, ref Rect to, ref int fromAlpha, ref int toAlpha, ref long duration)
    {
        Rect screen = _scene.Bounds;
        Rect now = t.Window.Bounds.Intersect(screen), was = t.From.Intersect(screen);
        switch (t.Change)
        {
            case WindowChange.Closed:
            case WindowChange.Minimized:
                from = t.Change == WindowChange.Closed ? t.From.Inflate(Theme.ShadowSize).Intersect(screen) : was;
                if (from.IsEmpty) { return false; }
                _motionLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, from);
                ComposeBase(barMoves, shown, docked, false);
                CopyBase();
                Rect? pill = t.Change == WindowChange.Minimized ? _taskbar.PillOf(t.Window) : null;
                to = pill ?? Shrunk(from);
                toAlpha = pill is null ? 0 : PillAlpha;
                duration = t.Change == WindowChange.Closed ? CloseMicros : MinimizeMicros;
                return true;
            case WindowChange.Resized:
                if (Windows.Top != t.Window || was.IsEmpty || now.IsEmpty) { return false; }
                _motionLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, was);
                ComposeBase(barMoves, shown, docked, true);
                CopyBase();
                ComposeScene();
                from = was; to = now;
                duration = ResizeMicros;
                return true;
            default:
                to = t.Change == WindowChange.Opened ? t.Window.Bounds.Inflate(Theme.ShadowSize).Intersect(screen) : now;
                if (Windows.Top != t.Window || to.IsEmpty) { return false; }
                ComposeBase(barMoves, shown, docked, true);
                CopyBase();
                ComposeScene();
                _motionLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, to);
                Rect? origin = t.Change == WindowChange.Restored ? _taskbar.PillOf(t.Window) : null;
                from = origin ?? Shrunk(to);
                fromAlpha = origin is null ? 0 : PillAlpha;
                duration = t.Change == WindowChange.Opened ? OpenMicros : MinimizeMicros;
                return true;
        }
    }

    private static long Micros(long timestamp) => timestamp / System.Math.Max(1, System.Diagnostics.Stopwatch.Frequency / 1_000_000);

    /// <summary>Fades the focused window in over the scene behind it, to measure what this machine can draw.</summary>
    private void StartMotionTest()
    {
        Window? window = Windows.Focused;
        if (window is null || window != Windows.Top || _motion.Active || !MotionMemory())
        {
            Zenith.Core.Log.Write("motion", "test not started");
            return;
        }

        ComposeScene(withoutTop: true);
        CopyBase();
        ComposeScene();
        Rect area = window.Bounds.Inflate(Theme.ShadowSize).Intersect(_scene.Bounds);
        _motionLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, area);
        _motion.Begin(_motionBase!, _motionLayer, Shrunk(area), area, 0, 255, Micros(FrameStats.Now), MotionTestMicros);
        _motionName = "test";
    }

    private bool _verifyRequested;
    /// <summary>The scene as shown against one composed from nothing (every window captures and
    /// blurs its backdrop again): stale damage or a capture that saw its own window differ.</summary>
    private void VerifyScene()
    {
        ComposeScene();
        _motionBase ??= new int[_frame.Pixels.Length];
        System.Array.Copy(_scene.Pixels, _motionBase, _motionBase.Length);
        _sceneKey++; _lowerValid = false; _composeAll = true;
        ComposeScene();
        int n = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, w = _scene.Width;
        for (int i = 0; i < _motionBase.Length; i++)
        {
            if (_motionBase[i] == _scene.Pixels[i]) { continue; }
            n++; int x = i % w, y = i / w;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        Zenith.Core.Log.Write("compose", "verify differs=" + n + (n > 0 ? " at " + x0 + "," + y0 + " .. " + x1 + "," + y1 : "")
            + " windows=" + string.Join("|", System.Linq.Enumerable.Select(Windows.Windows, v => v.Title + "@" + v.Bounds.X + "," + v.Bounds.Y + "," + v.Bounds.W + "," + v.Bounds.H)));
    }

    /// <summary>Times the three kinds of scene change, compares the cached result with a
    /// full redraw and reports to the log.</summary>
    private void RunBench()
    {
        long Run(System.Action before)
        {
            FrameStats.TakePhases(1);
            long start = FrameStats.Now;
            for (int i = 0; i < BenchFrames; i++) { before(); ComposeScene(); }
            return (Micros(FrameStats.Now) - Micros(start)) / BenchFrames;
        }

        // Compare before timing, so a stale cache is seen as it was.
        ComposeScene();
        _motionBase ??= new int[_frame.Pixels.Length];
        System.Array.Copy(_scene.Pixels, _motionBase, _motionBase.Length);
        Windows.InvalidateLower();
        ComposeScene();
        int differ = 0;
        for (int i = 0; i < _motionBase.Length; i++) { if (_motionBase[i] != _scene.Pixels[i]) { differ++; } }

        // Cold: a new scene key, so every window blurs again. Full: the lower windows are redrawn
        // (as when one of them updates) but keep blurs nothing changed under. Both must match exactly.
        long cold = Run(() => { _lowerValid = false; });
        string coldPhases = FrameStats.TakePhases(BenchFrames);
        _lowerValid = false; ComposeScene();
        System.Array.Copy(_scene.Pixels, _motionBase, _motionBase.Length);
        Windows.InvalidateLower(); ComposeScene();
        int cached = 0;
        for (int i = 0; i < _motionBase.Length; i++) { if (_motionBase[i] != _scene.Pixels[i]) { cached++; } }
        long full = Run(Windows.InvalidateLower);
        string fullPhases = FrameStats.TakePhases(BenchFrames);
        // Partial composes (R2): a top window and a lower window drawing new content. Each must leave
        // exactly the pixels a full compose of the same state leaves.
        int PartialDiffers(System.Action change)
        {
            change(); ComposeScene();
            System.Array.Copy(_scene.Pixels, _motionBase, _motionBase.Length);
            _lowerValid = false; ComposeScene();
            int n = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int i = 0; i < _motionBase.Length; i++)
            {
                if (_motionBase[i] == _scene.Pixels[i]) { continue; }
                n++; int x = i % _scene.Width, y = i / _scene.Width;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            if (n > 0) { Zenith.Core.Log.Write("compose", "partial differs at " + x0 + "," + y0 + " .. " + x1 + "," + y1 + " windows=" + string.Join("|", System.Linq.Enumerable.Select(Windows.Windows, w => w.Title + "@" + w.Bounds.X + "," + w.Bounds.Y + "," + w.Bounds.W + "," + w.Bounds.H)) + " bar=" + _taskbar.Bounds.X + "," + _taskbar.Bounds.Y + "," + _taskbar.Bounds.W + "," + _taskbar.Bounds.H); }
            return n;
        }
        Window? topWindow = Windows.Top, lowerWindow = null;
        foreach (Window w in Windows.Windows) { if (w != topWindow && !w.IsMinimized) { lowerWindow = w; } }
        int topDiffers = topWindow is null ? -1 : PartialDiffers(() => Windows.MarkChanged(topWindow));
        int lowerDiffers = lowerWindow is null ? -1 : PartialDiffers(() => Windows.MarkChanged(lowerWindow));
        ComposeScene();
        long partialTop = topWindow is null ? 0 : Run(() => Windows.MarkChanged(topWindow));
        string partialTopPhases = FrameStats.TakePhases(BenchFrames);
        long partialLower = lowerWindow is null ? 0 : Run(() => Windows.MarkChanged(lowerWindow));
        string partialLowerPhases = FrameStats.TakePhases(BenchFrames);
        long top = Run(static () => { });
        string topPhases = FrameStats.TakePhases(BenchFrames);
        int step = 1;
        long drag = Run(() => { Windows.NudgeTop(step); step = -step; });
        string dragPhases = FrameStats.TakePhases(BenchFrames);
        Zenith.Core.Log.Write("compose", "bench windows=" + Windows.Windows.Count + " materials=" + MaterialResources.Policy.Effective + " verify=" + differ
            + " partial-differs=" + topDiffers + "/" + lowerDiffers + " partial-top=" + partialTop + " [" + partialTopPhases + "] partial-lower=" + partialLower + " [" + partialLowerPhases + "]"
            + " cached-differs=" + cached + " cold=" + cold + " [" + coldPhases + "] full=" + full + " [" + fullPhases + "] top=" + top + " [" + topPhases + "] drag=" + drag + " [" + dragPhases + "]");
    }

    /// <summary>Compare actual whole-scene composition with identical inputs and
    /// cold material captures. All windows and presentation remain on this thread.</summary>
    private void RunParallelBench(bool fullscreen)
    {
        bool saved = CpuWorkers.ParallelBlur;
        Window? resized = fullscreen && Windows.Top is Window top && top.CanResize && !top.IsFullscreen ? top : null;
        if (resized is not null) { Windows.ToggleFullscreen(resized); }
        try
        {
            void Compose(bool parallel)
            {
                CpuWorkers.ParallelBlur = parallel;
                _lowerValid = false; Windows.InvalidateLower(); ComposeScene();
            }
            Compose(false); Compose(true); // Equal full-path warm-up.
            Compose(false);
            int[] baseline = new int[_scene.Pixels.Length];
            System.Array.Copy(_scene.Pixels, baseline, baseline.Length);
            long tiles = CpuWorkers.BlurTiles;
            Compose(true);
            if (Windows.Top is Window visible)
            {
                Rect b = visible.Bounds;
                Zenith.Core.Log.Write("compose", "parallel viewport=" + b.X + "," + b.Y + "," + b.W + "," + b.H);
            }
            int differ = 0;
            for (int i = 0; i < baseline.Length; i++) { if (baseline[i] != _scene.Pixels[i]) { differ++; } }
            long Time(bool parallel)
            {
                long start = FrameStats.Now;
                for (int i = 0; i < 3; i++) { Compose(parallel); }
                return (Micros(FrameStats.Now) - Micros(start)) / 3;
            }
            long scalar = Time(false), distributed = Time(true);
            Zenith.Core.Log.Write("compose", "parallel bench verify=" + differ + " scalar-us=" + scalar + " parallel-us=" + distributed
                + " ap-tiles=" + (CpuWorkers.BlurTiles - tiles) + " materials=" + MaterialResources.Policy.Effective);
            Zenith.Core.Log.Write("compose", CpuWorkers.BlurStatus);
            CpuWorkers.ParallelBlur = saved;
            if (resized is not null) { Windows.ToggleFullscreen(resized); }
        }
        catch
        {
            CpuWorkers.ParallelBlur = saved;
            if (resized is not null) { Windows.ToggleFullscreen(resized); }
            throw;
        }
    }

    /// <summary>Identifies the control under the pointer; a change means hover highlights need redrawing.</summary>
    private long HoverKey()
    {
        int x = _input.X, y = _input.Y;
        long key = _prism.IsOpen ? _prism.HoverKey(x, y) : 0;
        key = key * 1024 + _taskbar.HoverKey(x, y);
        return key * 4096 + Windows.HoverKey(x, y);
    }

    private void HandleInput()
    {
        foreach (KeyEvent key in _input.Keys)
        {
            if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == (ConsoleModifiers.Control | ConsoleModifiers.Alt))
            {
                if (key.Key == ConsoleKeyEx.T)
                {
                    OpenApp((key.Modifiers & ConsoleModifiers.Shift) != 0 ? RecoveryTerminal : "Terminal", null);
                    return;
                }
                if (key.Key == ConsoleKeyEx.L) { Zenith.Core.Sessions.SessionRuntime.RequestLogout(); return; }
            }
            if (key.Key == ConsoleKeyEx.F11 && Windows.Focused is Window focused) { Windows.ToggleFullscreen(focused); return; }
            if (key.Key == ConsoleKeyEx.Escape && _taskbar.OverflowOpen) { _taskbar.OverflowOpen = false; return; }
        }
        // Prism: Alt+Space or Meta opens the palette from anywhere; while open it owns the keyboard
        // and the next click.
        foreach (KeyEvent key in _input.Keys)
        {
            bool palette = key.Key is ConsoleKeyEx.LWin or ConsoleKeyEx.RWin
                || (key.Key == ConsoleKeyEx.Spacebar && (key.Modifiers & ConsoleModifiers.Alt) != 0 && (key.Modifiers & ConsoleModifiers.Control) == 0);
            if (palette)
            {
                if (_prism.IsOpen && _prism.Mode == PrismView.Layout.Palette) { _prism.Close(); }
                else { _prism.Open(PrismView.Layout.Palette, _taskbar.Bounds, _scene.Width, _scene.Height); }
                return;
            }
            if (_prism.IsOpen) { _prism.HandleKey(key); }
        }
        bool menuWasOpen = _prism.IsOpen;
        if (menuWasOpen)
        {
            if (!_input.LeftPressed) { return; }
            _prism.HandleClick(_input.X, _input.Y);
            if (!_taskbar.Bounds.Contains(_input.X, _input.Y)) { return; }
        }

        if (_input.LeftPressed && _taskbar.HandleClick(_input.X, _input.Y, out bool launcherClicked))
        {
            if (launcherClicked)
            {
                if (menuWasOpen) { _prism.Close(); }
                else
                {
                    var layout = Zenith.Core.Configuration.SystemConfiguration.Settings.Current.Launcher == 1 ? PrismView.Layout.Palette : PrismView.Layout.Menu;
                    _prism.Open(layout, _taskbar.Bounds, _scene.Width, _scene.Height);
                }
            }

            return;
        }

        Windows.HandleInput(_input);
    }

    private void ComposeScene(bool withoutTop = false)
    {
        long start = FrameStats.Now;
        MaterialResources.Policy.Begin(NativeAppearance.Current.Materials, NativeAppearance.Current.Translucency);
        // A new scene key makes every window blur its backdrop again; otherwise windows keep
        // their blur unless something under them changed.
        bool appearance = AppearanceStillValid();
        if (!appearance) { _sceneKey++; }
        Window? top = Windows.Top;
        Rect topArea = top is null ? new Rect(0, 0, 0, 0) : WindowManager.AreaOf(top);
        long topKey = TopKey(top), barKey = BarKey();
        Rect barArea = _taskbar.Shown ? _taskbar.Bounds.Inflate(Theme.ShadowSize + 8) : new Rect(0, 0, 0, 0);
        bool overlays = withoutTop || _prism.IsOpen || _taskbar.OverflowOpen || Zenith.Core.Sessions.SessionRuntime.Message is not null;
        // After an animation or a pass without the top window, the scene does not hold a finished
        // frame: compose everything once.
        bool structural = _composeAll || !appearance || !_lowerValid || top != _lastTop || !Windows.LowerStructureUnchanged();
        if (structural || overlays)
        {
            // Everything: the scene below the top window (when it changed), the top window, the shell.
            _lowerRedrawn = !appearance || !_lowerValid || !Windows.LowerUnchanged(_input.X, _input.Y);
            FrameStats.FullComposes++;
            long lowerStart = FrameStats.Now;
            if (_lowerRedrawn)
            {
                _scene.CopyFrom(_wallpaper);
                Windows.DrawLower(_scene, _input.X, _input.Y, _sceneKey);
                _lower.CopyFrom(_scene);
                _lowerSpent = MaterialResources.Policy.Spent;
                _lowerValid = true;
                RememberLowerAppearance();
            }
            else
            {
                _scene.CopyFrom(_lower);
                MaterialResources.Policy.Restore(_lowerSpent);
            }
            long topStart = FrameStats.Now; FrameStats.LowerTicks += topStart - lowerStart;
            if (!withoutTop) { Windows.DrawTop(_scene, _lower, _input.X, _input.Y, _sceneKey); }
            FrameStats.TopTicks += FrameStats.Now - topStart;
            _composeDamage = _scene.Bounds;
        }
        else
        {
            // Only what changed: lower windows that changed, the top window if it did, the bar if it
            // did or the damage reaches it. The scene keeps last frame's pixels everywhere else.
            Rect damage = new(0, 0, 0, 0);
            long lowerStart = FrameStats.Now;
            if (!Windows.LowerUnchanged(_input.X, _input.Y))
            {
                // An empty damage still records the new lower state (input reached a lower window).
                damage = Windows.LowerDamage(_input.X, _input.Y);
                Windows.DrawLowerPartial(_lower, _wallpaper, ref damage, _input.X, _input.Y, _sceneKey);
            }
            MaterialResources.Policy.Restore(_lowerSpent);
            _lowerRedrawn = false;
            // The top window's blur changes around what changed under it; new content only where it differs.
            damage = Windows.GrowForTop(damage, damage);
            if (topKey != _lastTopKey) { damage = damage.Union(topArea).Union(_lastTopRect); }
            else if (top is not null && top.ContentVersion != _lastTopVersion)
            { damage = damage.Union(top.ContentDamage.IsEmpty ? topArea : top.ContentDamage); }
            if (barKey != _lastBarKey || _barChanged) { damage = damage.Union(barArea).Union(_lastBarRect); }
            Rect barHit = barArea.Intersect(damage);
            if (barHit.W > 0 && barHit.H > 0) { damage = damage.Union(barArea); }
            damage = damage.Intersect(_scene.Bounds);
            _composeDamage = damage;
            long topStart = FrameStats.Now; FrameStats.LowerTicks += topStart - lowerStart;
            if (!damage.IsEmpty)
            {
                _scene.BaseClip = damage; MaterialComposition.Limit = damage;
                _scene.CopyRect(_lower, damage);
                Windows.DrawTop(_scene, _lower, _input.X, _input.Y, _sceneKey);
                // Its capture changed it everywhere (a new adapted opacity): draw all of it once more.
                Rect whole = topArea.Intersect(_scene.Bounds);
                if (Windows.TopWhollyChanged && !whole.IsEmpty && damage.Union(whole) is Rect grown
                    && (grown.W != damage.W || grown.H != damage.H || grown.X != damage.X || grown.Y != damage.Y))
                {
                    damage = grown; _composeDamage = damage;
                    _scene.BaseClip = damage; MaterialComposition.Limit = damage;
                    _scene.CopyRect(_lower, damage);
                    Windows.DrawTop(_scene, _lower, _input.X, _input.Y, _sceneKey);
                }
            }
            FrameStats.TopTicks += FrameStats.Now - topStart;
        }
        _lastTopVersion = top?.ContentVersion ?? 0;
        _lastTop = top; _lastTopKey = topKey; _lastTopRect = topArea; _lastBarKey = barKey; _lastBarRect = barArea; _barChanged = false;
        // Overlays (Prism, the overflow list, a message) are drawn over the scene, not into the lower
        // layers: the frame after one closes must compose everything to take its pixels away.
        _composeAll = overlays;
        FrameStats.ComposeCount++;
        if (_composeDamage.IsEmpty) { FrameStats.RecordCompose(start); return; }

        long shell = FrameStats.Now;
        _taskbar.LauncherOpen = _prism.IsOpen && _prism.Mode == PrismView.Layout.Menu;
        Rect barTouch = (_taskbar.Shown ? _taskbar.Bounds.Inflate(Theme.ShadowSize + 8) : new Rect(0, 0, 0, 0)).Intersect(_composeDamage);
        if (_taskbar.Shown && barTouch.W > 0 && barTouch.H > 0) { _taskbar.Draw(_scene, _input.X, _input.Y); }
        else { _taskbar.Layout(); }
        if (Zenith.Core.Sessions.SessionRuntime.Message is string message)
        {
            _scene.FillRoundRect(new Rect(12, 12, Math.Min(WidthForMessage(), _scene.Width - 24), 40), 6, Theme.Panel);
            _scene.DrawText(message, Fonts.Regular, 13, Theme.TextPrimary, 24, 24);
        }
        if (_prism.IsOpen) { _prism.Draw(_scene, _input.X, _input.Y); }
        if (_taskbar.Shown) { _taskbar.DrawOverflowList(_scene, _input.X, _input.Y); }

        FrameStats.ShellTicks += FrameStats.Now - shell;
        _scene.BaseClip = _scene.Bounds; MaterialComposition.Limit = MaterialComposition.Unlimited;
        var policy = MaterialResources.Policy;
        Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(7, policy.Effective, policy.Reason);
        FrameStats.RecordCompose(start);
    }
    private int WidthForMessage() => 520;

    private long _windowsPublishedAt;
    /// <summary>Observatory's Apps page: the windows and their programs, twice a second at most.</summary>
    private void PublishWindows()
    {
        long now = FrameStats.Now;
        if (now - _windowsPublishedAt < System.Diagnostics.Stopwatch.Frequency / 2) { return; }
        _windowsPublishedAt = now;
        var list = new System.Collections.Generic.List<Zenith.Core.Monitoring.ActivityWindow>();
        foreach (var w in Windows.Windows)
        {
            if (w.IsClosed) { continue; }
            list.Add(new(w.OwnerPid, w.Title, w.IsFocused, w.IsMinimized));
        }
        Zenith.Core.Monitoring.KernelMonitor.Windows = list.ToArray();
    }

    private long _sceneKey, _wallpaperKey, _launchingUntil;
    private int _launchingWindows;

    /// <summary>The pointer's shape now: Working while an app starts (until its window opens), the
    /// arrow over Prism and the taskbar, else what the window manager says.</summary>
    private CursorShape PointerShape()
    {
        if (_launchingUntil != 0 && (FrameStats.Now > _launchingUntil || Windows.Windows.Count > _launchingWindows)) { _launchingUntil = 0; }
        if (_launchingUntil != 0) { return CursorShape.Progress; }
        if (_prism.IsOpen || (_taskbar.Shown && _taskbar.Bounds.Contains(_input.X, _input.Y))) { return CursorShape.Default; }
        return Windows.CursorAt(_input.X, _input.Y);
    }
    private bool AppearanceStillValid() => _lowerValid
        && _lowerMaterials == MaterialResources.Policy.Requested && _lowerTranslucency == MaterialResources.Policy.Translucency
        && _lowerPanel == Theme.Panel && _lowerBackground == Theme.WindowBackground && _lowerBorder == Theme.WindowBorder
        && _lowerFocused == Theme.WindowBorderFocused && _lowerText == Theme.TextPrimary && _lowerAccent == Theme.Accent;

    private void RememberLowerAppearance()
    {
        _lowerMaterials = MaterialResources.Policy.Requested; _lowerTranslucency = MaterialResources.Policy.Translucency;
        _lowerPanel = Theme.Panel; _lowerBackground = Theme.WindowBackground; _lowerBorder = Theme.WindowBorder;
        _lowerFocused = Theme.WindowBorderFocused; _lowerText = Theme.TextPrimary; _lowerAccent = Theme.Accent;
    }

    private void DrawCursor()
    {
        _cursorX = _input.X;
        _cursorY = _input.Y;
        Cursor.Draw(_frame, _cursorX, _cursorY);
    }

    private void Present() => Present(_frame.Bounds);
    private void Present(Rect damage)
    {
        long start = FrameStats.Now;
        _presenter.Present(_screen, _frame.Pixels, _frame.Width, _frame.Height, damage);
        _lastPresent = FrameStats.Now;
        FrameStats.RecordPresent(start);
    }

    private readonly FramePresenter _presenter = new();
    private long _lastPresent;
    private bool _framePending;
    // Up to 240 frames a second while something moves; idle frames cost nothing.
    private static readonly long FrameInterval = System.Diagnostics.Stopwatch.Frequency / 240;
    private bool _lowerRedrawn;
    /// <summary>True while a frame is due or being paced: the kernel loop keeps running instead of halting.</summary>
    public bool FramePending => _framePending;

    private Rect _composeDamage, _lastTopRect, _lastBarRect;
    private Window? _lastTop;
    private long _lastTopKey, _lastBarKey;
    private int _lastTopVersion;
    private bool _barChanged, _composeAll, _hardwarePointer;
    /// <summary>Everything that changes how the top window looks: place, state, hover, content.</summary>
    private long TopKey(Window? top)
    {
        if (top is null) { return 0; }
        Rect b = top.Bounds;
        int hover = Windows.HoverButton(top, _input.X, _input.Y);
        return HashCode.Combine(HashCode.Combine(b.X, b.Y, b.W, b.H), top.IsFocused, top.IsMaximized, top.IsFullscreen, top.Title, hover);
    }
    /// <summary>Everything that changes how the bar looks besides its own updates: windows' titles,
    /// focus and minimized state (pills), its shape, and the pointer while over it.</summary>
    private long BarKey()
    {
        var hash = new HashCode();
        hash.Add(_taskbar.Shown); hash.Add(_taskbar.Docked); hash.Add(_taskbar.Bounds.Y);
        foreach (Window w in Windows.Windows) { hash.Add(w.Title); hash.Add(w.IsFocused); hash.Add(w.IsMinimized); }
        if (_taskbar.Bounds.Inflate(Theme.TaskbarMargin).Contains(_input.X, _input.Y)) { hash.Add(_input.X); hash.Add(_input.Y); }
        return hash.ToHashCode();
    }
}
