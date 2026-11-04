using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;
using Zenith.Apps;
using Zenith.Core.Shell;
using Zenith.Core.Multiprocessing;
using Zenith.Core.Security;
using Zenith.Gui.Graphics;
using Zenith.Gui.Motion;
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
    private LauncherMenu _launcher;
    private long _lowerStamp;
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
        Wallpaper.Render(_wallpaper);

        Windows = new WindowManager(new Rect(0, 0, w, h - Theme.TaskbarHeight - 2 * Theme.TaskbarMargin), new Rect(0, 0, w, h));
        _taskbar = new Taskbar(w, h, Windows);
        _launcher = new LauncherMenu(_taskbar.Bounds, OpenApp);

        SessionDisplay.ConfigurePointer();
        MouseManager.SetPosition(w * SessionDisplay.Scale / 2, h * SessionDisplay.Scale / 2);

        TerminalWindow.OpenApp = OpenApp;
        FileManagerWindow.OpenApp = OpenApp;
        CommandShell.Register(new Command("fps", "fps [reset|motion|bench|cpu|parallel-bench|parallel on|off]", "Show compositor statistics", c =>
        {
            if (c.Args.Length == 2 && c.Args[0] == "parallel" && (c.Args[1] == "on" || c.Args[1] == "off"))
            {
                GuardedFilesystem.RequireRoot(); CpuWorkers.ParallelBlur = c.Args[1] == "on";
                c.WriteLine(CpuWorkers.BlurStatus); return 0;
            }
            if (c.Args.Length == 1 && c.Args[0] == "reset") { FrameStats.ResetWindow(); return 0; }
            // The desktop tick owns the windows, so the test runs there and reports to the log.
            if (c.Args.Length == 1 && c.Args[0] == "motion") { _motionTestRequested = true; return 0; }
            if (c.Args.Length == 1 && c.Args[0] == "cpu")
            {
                GuardedFilesystem.RequireRoot(); _cpuPreviewRequested = true; return 0;
            }
            if (c.Args.Length == 1 && c.Args[0] == "bench") { _benchRequested = true; return 0; }
            if (c.Args.Length >= 1 && c.Args[0] == "parallel-bench" && (c.Args.Length == 1 || (c.Args.Length == 2 && c.Args[1] == "fullscreen")))
            { GuardedFilesystem.RequireRoot(); _parallelBenchFullscreen = c.Args.Length == 2; _parallelBenchRequested = true; return 0; }
            if (c.Args.Length != 0) { return c.Fail("usage: fps [reset|motion|bench|cpu|parallel-bench|parallel on|off]"); }
            c.WriteLine("frames presented: " + FrameStats.Frames + ", scene composes: " + FrameStats.SceneComposes);
            c.WriteLine("last compose: " + FrameStats.LastComposeMicros + " us, last present: " + FrameStats.LastPresentMicros + " us");
            c.WriteLine("display: " + _screen.Name + " " + _screen.Width + "x" + _screen.Height);
            c.WriteLine("since reset: " + FrameStats.SceneFrames + " scene frames, average " + FrameStats.AverageFrameMicros + " us, worst " + FrameStats.MaxFrameMicros + " us");
            c.WriteLine("worst compose: " + FrameStats.MaxComposeMicros + " us, worst present: " + FrameStats.MaxPresentMicros + " us");
            c.WriteLine("over 16 ms: " + FrameStats.FramesOver16Ms + ", over 33 ms: " + FrameStats.FramesOver33Ms);
            c.WriteLine(CpuWorkers.BlurStatus);
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
    public bool OpenApp(string name, string? argument)
    {
        // Always the built-in terminal: the way back when an installed Polaris does not work.
        if (string.Equals(name, RecoveryTerminal, System.StringComparison.OrdinalIgnoreCase)) { Windows.Open(new TerminalWindow()); return true; }
        if (string.Equals(name, "Polaris", System.StringComparison.OrdinalIgnoreCase)) { name = "Terminal"; }
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
                    if (!_apps.Launch(native, args.ToArray(), cwd)) { Windows.Open(new AppLaunchNotice("Too many applications are open. Close one and try again.")); }
                }
                else { Windows.Open(app.Create(argument)); }
                return true;
            }
        }

        return false;
    }

    internal sealed class DisplayFrames
    {
        public readonly Surface Wallpaper, Lower, Scene, Frame;
        public readonly Taskbar Bar;
        public readonly LauncherMenu Launcher;
        public readonly System.Collections.Generic.List<(Window Window,Rect Bounds,Rect Restore,Rect Windowed,bool Maximized,bool Fullscreen)> Geometry=new();
        public DisplayFrames(int width,int height,WindowManager windows,System.Func<string,string?,bool> open)
        {
            Wallpaper=new Surface(width,height); Lower=new Surface(width,height); Scene=new Surface(width,height); Frame=new Surface(width,height);
            Zenith.Gui.Graphics.Wallpaper.Render(Wallpaper); Bar=new Taskbar(width,height,windows); Launcher=new LauncherMenu(Bar.Bounds,open);
        }
        public DisplayFrames(Desktop desktop)
        {
            Wallpaper=desktop._wallpaper; Lower=desktop._lower; Scene=desktop._scene; Frame=desktop._frame;
            Bar=desktop._taskbar; Launcher=desktop._launcher;
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
        _taskbar=frames.Bar; _launcher=frames.Launcher;
        _barMode=(TaskbarMode)(-1); _lowerValid=false; _sceneDirty=true; _cursorDirty=true; _hoverKey=-1;
        Zenith.Core.Processes.NativeSystemInformation.Display(w+"x"+h+" logical, scale="+SessionDisplay.Scale);
    }
    public WindowManager Windows { get; }

    public void StopClients() { _taskbar.Dispose(); Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(0, 0, 2); _apps.Stop(); _nativeClients.Stop(); Zenith.Core.Processes.NativeSystemInformation.Display("No graphical session"); }

    /// <summary>What is on screen, for the crossfade when this session ends.</summary>
    public int[] FramePixels => _frame.Pixels;
    private const string RecoveryTerminal = "recovery-terminal";
    public bool TerminalBusy => _nativeClients.TerminalBusy;

    public void Tick()
    {
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

        // The recompose at the end of an animation catches up on everything else.
        if (interrupts) { _motion.Interrupt(); }
        if (Windows.Update() | _taskbar.Update())
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
        if (_parallelBenchRequested) { _parallelBenchRequested = false; RunParallelBench(_parallelBenchFullscreen); _sceneDirty = true; }
        if (_motionTestRequested) { _motionTestRequested = false; StartMotionTest(); }
        StartTransition();
        if (_motion.Active || _motionName is not null)
        {
            if (DrawMotionFrame())
            {
                _sceneDirty = false;
                _cursorDirty = false;
                return;
            }
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
        return (TaskbarPolicy.Shown(mode, fullscreen, maximized, now < _barPointerUntil, _launcher.IsOpen || _taskbar.OverflowOpen),
                TaskbarPolicy.Docked(mode, fullscreen, maximized));
    }

    private void SetBar(bool shown, bool docked)
    {
        _taskbar.Shown = shown;
        _taskbar.Docked = docked;
        _launcher.AlignTo(_taskbar.Bounds);
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
        if (!_motion.Render(_frame.Pixels, _frame.Width, _frame.Height, Micros(start)))
        {
            ReportMotion();
            return false;
        }
        DrawCursor();
        Present();
        _motion.RecordFrame(Micros(FrameStats.Now) - Micros(start));
        return true;
    }

    private void ReportMotion()
    {
        if (_motionName is null) { return; }
        Zenith.Core.Log.Write("motion", _motionName + " frames=" + _motion.Frames + " elapsed=" + _motion.ElapsedMicros + " worst=" + _motion.WorstFrameMicros + " ended=" + _motion.LastEnd);
        _motionName = null;
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

        long full = Run(Windows.InvalidateLower);
        string fullPhases = FrameStats.TakePhases(BenchFrames);
        long top = Run(static () => { });
        string topPhases = FrameStats.TakePhases(BenchFrames);
        int step = 1;
        long drag = Run(() => { Windows.NudgeTop(step); step = -step; });
        string dragPhases = FrameStats.TakePhases(BenchFrames);
        Zenith.Core.Log.Write("compose", "bench windows=" + Windows.Windows.Count + " materials=" + MaterialResources.Policy.Effective + " verify=" + differ
            + " full=" + full + " [" + fullPhases + "] top=" + top + " [" + topPhases + "] drag=" + drag + " [" + dragPhases + "]");
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

    private void ComposeScene(bool withoutTop = false)
    {
        long start = FrameStats.Now;
        MaterialResources.Policy.Begin(NativeAppearance.Current.Materials, NativeAppearance.Current.Translucency);
        if (!LowerStillValid())
        {
            _scene.CopyFrom(_wallpaper);
            Windows.DrawLower(_scene, _input.X, _input.Y);
            _lower.CopyFrom(_scene);
            _lowerSpent = MaterialResources.Policy.Spent;
            _lowerStamp++;
            _lowerValid = true;
            RememberLowerAppearance();
        }
        else
        {
            _scene.CopyFrom(_lower);
            MaterialResources.Policy.Restore(_lowerSpent);
        }
        if (!withoutTop) { Windows.DrawTop(_scene, _input.X, _input.Y, _lowerStamp); }

        long shell = FrameStats.Now;
        _taskbar.LauncherOpen = _launcher.IsOpen;
        if (_taskbar.Shown) { _taskbar.Draw(_scene, _input.X, _input.Y); }
        else { _taskbar.Layout(); }
        if (Zenith.Core.Sessions.SessionRuntime.Message is string message)
        {
            _scene.FillRoundRect(new Rect(12, 12, Math.Min(WidthForMessage(), _scene.Width - 24), 40), 6, Theme.Panel);
            _scene.DrawText(message, Fonts.Regular, 13, Theme.TextPrimary, 24, 24);
        }
        if (_launcher.IsOpen)
        {
            _launcher.Draw(_scene, _input.X, _input.Y);
        }
        if (_taskbar.Shown) { _taskbar.DrawOverflowList(_scene, _input.X, _input.Y); }

        FrameStats.ShellTicks += FrameStats.Now - shell;
        var policy = MaterialResources.Policy;
        Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(7, policy.Effective, policy.Reason);
        FrameStats.RecordCompose(start);
    }
    private int WidthForMessage() => 520;

    private bool LowerStillValid() => _lowerValid
        && _lowerMaterials == MaterialResources.Policy.Requested && _lowerTranslucency == MaterialResources.Policy.Translucency
        && _lowerPanel == Theme.Panel && _lowerBackground == Theme.WindowBackground && _lowerBorder == Theme.WindowBorder
        && _lowerFocused == Theme.WindowBorderFocused && _lowerText == Theme.TextPrimary && _lowerAccent == Theme.Accent
        && Windows.LowerUnchanged(_input.X, _input.Y);

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

    private void Present()
    {
        long start = FrameStats.Now;
        _screen.DrawArray(_frame.Pixels, 0, 0, _frame.Width, _frame.Height);
        _screen.Display();
        FrameStats.RecordPresent(start);
    }
}
