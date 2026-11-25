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
internal sealed partial class Desktop : Zenith.Gui.Shell.Sunset.ISunsetHost
{
    private Canvas _screen;
    private Surface _wallpaper;
    private Surface _lower;   // wallpaper and every window below the top one
    private Surface _scene;   // everything but the cursor
    private Surface _frame;   // scene + cursor, what is on screen
    private readonly Input _input = new();
    private Window? _keyEdgesOwner;
    private NativeAppLauncher _apps = new();
    private NativeClientHost _nativeClients;
    private Taskbar _taskbar;
    private PrismView _prism;
    private readonly Toasts _toasts = new();
    private readonly Zenith.Gui.Shell.Portal.PortalHost _portal = new();
    private readonly Zenith.Gui.Shell.Sunset.SunsetView _sunset;
    private readonly Zenith.Core.Sessions.IdleLockTimer _idleLock = new();
    private readonly Zenith.Gui.Shell.Sunset.LockScreen _lock = new();
    private const long SunsetOpenMicros = 120_000;
    // Cosmos reports no key releases: Meta followed within three seconds by Shift+L counts as Meta+Shift+L (a memory
    // collection can hold the desktop for over a second between the two; kernel/v0.0.73 missed it at one).
    private long _metaAt;
    private Zenith.Core.Sessions.SunsetAction? _pendingPower;
    private long _pendingPowerFrame;
    private readonly System.Collections.Generic.List<Window> _prismWindows = new();
    private bool _lowerValid;
    private bool _snapShown; private int _snapZone;
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

    public Desktop(Canvas screen, bool tiling = false)
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

        Rect screenArea = new(0, 0, w, h);
        Windows = new WindowManager(BarWorkArea(screenArea), screenArea, tiling);
        var preferences = Zenith.Core.Configuration.SystemConfiguration.Settings.DesktopPreferences;
        Windows.InitializeDesktops(preferences.Desktops);
        Windows.SetTileLayout(preferences.MasterPercent, preferences.TileInnerGap, preferences.TileOuterGap);
        _taskbar = CreateTaskbar(w, h);
        _toasts.RunAction = RunNotificationAction;
        // Meridian's wallpaper picker shows real previews, drawn by the same formulas (call 669).
        Zenith.Core.Configuration.StyleCatalog.RenderWallpaper = (choice, w, h) =>
        {
            var picture = new Surface(w, h);
            Wallpaper.Render(picture, choice, Theme.IsLight, Theme.Accent & 0xFFFFFF, quiet: true);
            return picture.Pixels;
        };
        Zenith.Core.Configuration.StyleCatalog.Accent = () => Theme.Accent & 0xFFFFFF;
        Zenith.Core.Configuration.StyleCatalog.RenderPreview = StylePreviews.Render;
        Zenith.Gui.Motion.OverlayMotion.AnimationPercent = () => Zenith.Core.Configuration.SystemConfiguration.Settings?.AnimationPercent ?? 100;
        Zenith.Gui.WindowManager.AnimationPercent = Zenith.Gui.Motion.OverlayMotion.AnimationPercent;
        Toasts.AnimationPercent = Zenith.Gui.Motion.OverlayMotion.AnimationPercent;
        Zenith.Gui.Motion.CountMotion.AnimationPercent = Zenith.Gui.Motion.OverlayMotion.AnimationPercent;
        Zenith.Core.Configuration.StyleCatalog.Values = field => Zenith.Core.Configuration.SystemConfiguration.Settings?.Style(field) ?? 0;
        _prism = new PrismView(PrismCandidates, PrismActivate, PrismUser);
        _portal.Attach();
        Zenith.Gui.Shell.Portal.OpenWithDialog.Launch = (app, path) => OpenAppWith(app, new[] { path });
        _sunset = new Zenith.Gui.Shell.Sunset.SunsetView(this);

        SessionDisplay.ConfigurePointer();
        MouseManager.SetPosition(SessionDisplay.Physical.Width / 2, SessionDisplay.Physical.Height / 2);
        Input.CaptureKeyEdges(true);

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
    /// <summary>A notification button: Restart asks Sunset (which confirms), "@sender" tells the program that posted it
    /// (event 13) and brings its window forward, an app name opens that app, an empty one only closes the notification.</summary>
    private void RunNotificationAction(Zenith.Core.Notifications.NotificationCenter.NoteAction action, Zenith.Core.Notifications.NotificationCenter.Note note, int index)
    {
        _sceneDirty = true;
        if (action.Restart) { OpenSunset(Zenith.Core.Sessions.SunsetAction.Restart); return; }
        if (action.App == "@sender")
        {
            foreach (var window in Windows.Windows) { if (window.OwnerPid == note.SenderPid && note.SenderPid != 0) { Windows.Activate(window); break; } }
            if (!_nativeClients.SendNotificationAction(note.SenderPid, index, note.Id)) { Zenith.Core.Log.Write("notify", "action for " + note.Source + " dropped: the program has no window"); }
            return;
        }
        if (action.App.Length > 0) { OpenAppWith(action.App, action.Args.Length == 0 ? null : action.Args); }
    }

    /// <summary>Opens an app by name with extra program arguments (Prism opens a Meridian page
    /// with "--page", ID). An installed native program runs; otherwise its built-in window.</summary>
    public bool OpenAppWith(string name, string[]? arguments)
    {
        // Always the built-in terminal: the way back when an installed Polaris does not work.
        if (string.Equals(name, RecoveryTerminal, System.StringComparison.OrdinalIgnoreCase)) { Windows.Open(new TerminalWindow { AppId = "Terminal" }); return true; }
        if (string.Equals(name, "Polaris", System.StringComparison.OrdinalIgnoreCase)) { name = "Terminal"; }
        if (AppRegistry.Find(name) is not AppInfo app) { return false; }
        if (app.Native is string native && Zenith.Core.Packages.NativePackageSource.State("/usr/share/zenith/native/" + native + ".elf") != Zenith.Core.Packages.PackagePathState.Missing)
        {
            var args = new System.Collections.Generic.List<string> { native };
            if (app.Action is not null) { args.Add(app.Action); }
            if (arguments is not null) { args.AddRange(arguments); }
            string user = Accounts.Service?.Name(CredentialContext.Current.Uid) ?? "user";
            string cwd = CredentialContext.Current.IsRoot ? "/root" : "/home/" + user;
            if (!_apps.Launch(native, args.ToArray(), cwd, new(app.Name, Windows.ActiveDesktopId))) { Windows.Open(new AppLaunchNotice("Too many applications are open. Close one and try again.")); }
            else { _launchingUntil = FrameStats.Now + 4 * System.Diagnostics.Stopwatch.Frequency; _launchingWindows = Windows.Windows.Count; }
        }
        else
        {
            Window window = app.Create(arguments is { Length: 1 } ? arguments[0] : null);
            window.AppId = app.Name;
            Windows.Open(window);
        }
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
        list.AddRange(PrismSearch.Actions());
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
                    { Windows.Activate(_prismWindows[index]); }
                    break;
                case PrismKind.Setting: OpenAppWith("Meridian", new[] { "--page", item.Target }); break;
                case PrismKind.Calculator: Zenith.Gui.Clipboard.Text = item.Target; break;
                case PrismKind.Command:
                    // The built-in terminal takes typed input; Polaris has no run-a-command option yet.
                    var terminal = new TerminalWindow { AppId = "Terminal" }; Windows.Open(terminal);
                    foreach (char c in item.Target) { terminal.OnKey(new KeyEvent(c, ConsoleKeyEx.NoName, false, false, false, KeyEvent.KeyEventType.Make)); }
                    terminal.OnKey(new KeyEvent('\n', ConsoleKeyEx.Enter, false, false, false, KeyEvent.KeyEventType.Make));
                    break;
                case PrismKind.Action:
                    var host = (Zenith.Gui.Shell.Sunset.ISunsetHost)this;
                    if (item.Target == "lock" && host.LockUnavailable is null) { host.Act(Zenith.Core.Sessions.SunsetAction.Lock); }
                    else
                    {
                        OpenSunset(item.Target switch
                        {
                            "logout" => Zenith.Core.Sessions.SunsetAction.LogOut, "restart" => Zenith.Core.Sessions.SunsetAction.Restart,
                            "shutdown" => Zenith.Core.Sessions.SunsetAction.ShutDown, _ => Zenith.Core.Sessions.SunsetAction.Lock,
                        });
                    }
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
        public DisplayFrames(int width,int height,Taskbar bar)
        {
            Wallpaper=new Surface(width,height); Lower=new Surface(width,height); Scene=new Surface(width,height); Frame=new Surface(width,height);
            Zenith.Gui.Graphics.Wallpaper.Render(Wallpaper); Bar=bar;
        }
        public DisplayFrames(Desktop desktop)
        {
            Wallpaper=desktop._wallpaper; Lower=desktop._lower; Scene=desktop._scene; Frame=desktop._frame;
            Bar=desktop._taskbar;
            foreach(Window w in desktop.Windows.Windows) { Geometry.Add((w,w.Bounds,w.RestoreBounds,w.WindowedBounds,w.IsMaximized,w.IsFullscreen)); }
        }
    }
    internal DisplayFrames PrepareDisplay(int width,int height) => new(width,height,CreateTaskbar(width,height));
    internal DisplayFrames CaptureDisplay() => new(this);
    internal void UseDisplay(Canvas screen,DisplayFrames frames)
    {
        _motion.Interrupt(); _motionBase=null; _screen=screen;
        _wallpaper=frames.Wallpaper; _lower=frames.Lower; _scene=frames.Scene; _frame=frames.Frame;
        int w=screen.Width,h=screen.Height;
        Rect screenArea = new(0, 0, w, h);
        Windows.SetDisplayArea(screenArea, BarWorkArea(screenArea));
        foreach(var item in frames.Geometry)
        {
            if(item.Window.IsClosed) { continue; }
            item.Window.IsMaximized=item.Maximized; item.Window.IsFullscreen=item.Fullscreen;
            item.Window.RestoreBounds=item.Restore; item.Window.WindowedBounds=item.Windowed; item.Window.SetBounds(item.Bounds);
        }
        Windows.RefreshAutoTiles();
        _taskbar=frames.Bar; _prism.Place(_taskbar.Bounds, w, h, _taskbar.Position);
        _barMode=(TaskbarMode)(-1); _lowerValid=false; _sceneDirty=true; _cursorDirty=true; _hoverKey=-1;
        Zenith.Core.Processes.NativeSystemInformation.Display(w+"x"+h+" logical, scale="+SessionDisplay.Percent+"%");
    }
    public WindowManager Windows { get; }

    private static Rect BarWorkArea(Rect screen)
    {
        var settings = Zenith.Core.Configuration.SystemConfiguration.Settings;
        return TaskbarPolicy.WorkArea(screen, (TaskbarMode)settings.Current.Taskbar,
            (TaskbarPosition)settings.DesktopPreferences.TaskbarPosition, Theme.TaskbarHeight, Theme.TaskbarMargin);
    }

    private Taskbar CreateTaskbar(int w, int h)
    {
        var bar = new Taskbar(w, h, Windows)
        {
            Position = (TaskbarPosition)Zenith.Core.Configuration.SystemConfiguration.Settings.DesktopPreferences.TaskbarPosition,
            Centers = new TaskbarCenters(OpenAppWith) { RunAction = RunNotificationAction },
            LaunchPin = OpenPinnedApp,
        };
        return bar;
    }

    private bool OpenPinnedApp(string id)
    {
        foreach (AppInfo app in AppRegistry.Visible())
        {
            if (string.Equals(app.Name, id, System.StringComparison.OrdinalIgnoreCase)) { return OpenAppWith(app.Name, null); }
        }
        return false;
    }

    public void StopClients() { Input.CaptureKeyEdges(false); _keyEdgesOwner?.ResetKeyEdges(); _portal.Detach(); _taskbar.Dispose(); _prism.Dispose(); _sunset.Dispose(); _lock.Dispose(); Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(0, 0, 2); _apps.Stop(); _nativeClients.Stop(); Zenith.Core.Processes.NativeSystemInformation.Display("No graphical session"); }

    /// <summary>What is on screen, for the crossfade when this session ends.</summary>
    public int[] FramePixels => _frame.Pixels;
    private const string RecoveryTerminal = "recovery-terminal";
    public bool TerminalBusy(Zenith.Core.Shell.Shell? requester) => _nativeClients.TerminalBusy(requester);

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
            _keyEdgesOwner?.ResetKeyEdges();
            WantedBar();
            ComposeScene();
            _frame.CopyFrom(_scene);
            DrawCursor();
            SessionDisplay.FadeTo(_frame.Pixels, _frame.Width, _frame.Height, welcome: true);
            Zenith.Core.Audio.SystemSounds.Play("login");
            _sceneDirty = false;
        }

        // Between the device pointer and one drawn into frames the whole frame is presented once:
        // the other kind's pointer must not stay on screen.
        bool hardwarePointer = HardwareCursor.Active;
        if (hardwarePointer != _hardwarePointer) { _hardwarePointer = hardwarePointer; _composeAll = true; _sceneDirty = true; }
        // A new cursor pack or size (or the accent for Comet) changes the pointer's pixels and its bounds.
        long cursorKey = Cursor.Key;
        if (cursorKey != _cursorKey) { _cursorKey = cursorKey; _composeAll = true; _sceneDirty = true; }
        // A new window decoration or widget style redraws every window, the cached lower ones too.
        long styleKey = (long)Decorations.Current | (long)Widgets.Current << 4 | (long)Widgets.Buttons << 8;
        if (styleKey != _styleKey)
        {
            if (_styleKey != -1) { _sceneKey++; Windows.InvalidateLower(); _lowerValid = false; _nativeClients.ResetAll(); }
            _styleKey = styleKey; _composeAll = true; _sceneDirty = true;
        }
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
        // A portal dialog (a program asking for permission) opens above everything and closes Prism.
        RunPendingPower();
        bool lockedAtStart = _lock.IsLocked;
        if (_lock.IsLocked) { if (_lock.Tick()) { _lockDirty = true; _sceneDirty = true; } }
        else if (_portal.Tick())
        {
            _sceneDirty = true; _composeAll = true;
            if (_portal.IsOpen) { _prism.Close(); _sunset.Close(); if (!_portalSounded) { _portalSounded = true; Zenith.Core.Audio.SystemSounds.Play("attention"); } }
            else { _portalSounded = false; }
        }
        if (_sunset.Tick(_scene.Width, _scene.Height)) { _sceneDirty = true; }
        // Notifications: toasts over the work area (not on the lock screen, where they wait).
        // System checks that become notifications: the disk filling up (at most once a minute).
        long checkNow = System.Diagnostics.Stopwatch.GetElapsedTime(0, FrameStats.Now).Ticks / 10;
        Zenith.Core.Storage.StorageWatch.Check(checkNow);
        Zenith.Core.Packages.PackageCenter.BackgroundCheck(checkNow);
        if (!_lock.IsLocked)
        {
            Window? front = Windows.Top;
            if (_toasts.Tick(Windows.WorkArea, _input.X, _input.Y, _taskbar.Centers?.Open == TaskbarCenters.Panel.Notifications,
                Windows.Focused is { IsMinimized: false } focused ? focused.OwnerPid : 0, front?.IsFullscreen == true))
            { _sceneDirty = true; _composeAll = true; }
        }
        if (_generation != Accounts.Generation)
        {
            foreach (var window in Windows.Windows) { if (window is TerminalWindow terminal && terminal.IsBusy) { return; } }
            _idleLock.Reset();
            _generation = Accounts.Generation;
            _apps.Stop(); _apps = new NativeAppLauncher();
            _nativeClients.Stop();
            _nativeClients = new NativeClientHost(Accounts.Session);
            Zenith.Core.Processes.NativeProcessGraphics.Register(_nativeClients.ForUser);
            foreach (var window in Windows.Windows) { window.EndSession(); }
            Zenith.Gui.Clipboard.Text = string.Empty;
            Windows.Update();
            Windows.InitializeDesktops(Zenith.Core.Configuration.SystemConfiguration.Settings.DesktopPreferences.Desktops);
            _workspaceGeneration = 0;
            using var identity = CredentialContext.Enter(Accounts.Session);
            Windows.Open(new TerminalWindow { AppId = "Terminal" });
            _sceneDirty = true;
        }
        UpdateWorkspacePreferences();
        if (_lock.IsLocked || _portal.IsOpen || _sunset.IsOpen || _prism.IsOpen) { CancelSwitcher(); }
        Window? beforeInput = KeyEdgesOwner();
        _input.Poll();
        bool altReleased = ReadWorkspaceKeys();
        long idleNow = FrameStats.Now / System.Diagnostics.Stopwatch.Frequency;
        bool physicalInput = _input.HasActivity || !_input.KeyEdges.IsEmpty || _input.LostKeyEdges;
        bool consumeWake = lockedAtStart && !_lock.IsLocked;
        if (consumeWake) { _idleLock.Reset(); _composeAll = _sceneDirty = true; }
        if (!_lock.IsLocked && !consumeWake &&
            _idleLock.Poll(idleNow, _desktopPreferences.IdleSeconds, physicalInput) is long lockDue &&
            ((Zenith.Gui.Shell.Sunset.ISunsetHost)this).LockUnavailable is null)
        {
            LockDesktop(_desktopPreferences.GraceSeconds == 0 ? 0 : lockDue + _desktopPreferences.GraceSeconds);
            consumeWake = physicalInput;
        }
        if (physicalInput && _lock.WakeDuringGrace(idleNow))
        {
            _idleLock.Reset(); consumeWake = _composeAll = _sceneDirty = true;
        }
        if (_lock.IsLocked || consumeWake) { _desktopModifiers = 0; }
        bool routeKeyEdges = !consumeWake;

        // A release or a hover change only redraws; the click that started an animation must not cut it short.
        bool interrupts = _input.Activity.InterruptsMotion;
        if (_input.Activity.LeftPressed || _input.Keys.Count > 0 || _input.Scroll != 0) { Zenith.Gui.Motion.OverlayMotion.Finish(); }
        if (!consumeWake && (_input.Activity.RoutesWithoutMotion || _switcherTabs != 0 || _tilingKeys.Count != 0))
        {
            routeKeyEdges = HandleInput(altReleased);
            _sceneDirty = true;
        }
        else if (!consumeWake && _input.Moved)
        {
            routeKeyEdges = HandleInput(altReleased);   // drags move windows on motion alone
            long hover = HoverKey();
            if (Windows.IsDragging || hover != _hoverKey)
            {
                _sceneDirty = true; _lockDirty = true;
                interrupts |= Windows.IsDragging;
            }
            else
            {
                _cursorDirty = true;
            }

            _hoverKey = hover;
        }

        if (altReleased && !_input.HasActivity && _switcher.Open)
        {
            _switcher.Accept(Windows);
            routeKeyEdges = false;
            _sceneDirty = _composeAll = true;
        }
        bool workspaceInput = routeKeyEdges && !_lock.IsLocked && !_portal.IsOpen && !_sunset.IsOpen && !_prism.IsOpen
            && !_switcher.Open && !_taskbar.PopupOpen && (!_taskbar.Shown || !_taskbar.Bounds.Contains(_input.X, _input.Y));
        if (Windows.UpdatePointerFocus(_input.X, _input.Y, workspaceInput, _input.Moved))
        { _sceneDirty = true; }
        Window? afterInput = KeyEdgesOwner();
        if (!routeKeyEdges || _input.LostKeyEdges || _keyEdgesOwner != beforeInput || beforeInput != afterInput)
        {
            // The desktop consumed this batch, or it straddled a focus change.
            // Clear held keys and let physical release/repress rearm the key.
            _keyEdgesOwner?.ResetKeyEdges();
        }
        else if (!_input.KeyEdges.IsEmpty) { afterInput?.OnKeyEdges(_input.KeyEdges); }
        _keyEdgesOwner = afterInput;

        CursorShape shape = PointerShape();
        if (shape != Cursor.Shape) { Cursor.Shape = shape; _cursorDirty = true; }

        // The recompose at the end of an animation catches up on everything else.
        if (interrupts) { _motion.Interrupt(); }
        bool barUpdated = _taskbar.Update();
        _barChanged |= barUpdated;
        if (Windows.Update() | barUpdated | _switcher.Prune())
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
        // A popup rising into place keeps the frames coming until it lands.
        if (Zenith.Gui.Motion.OverlayMotion.Active || Windows.SnapMoving || _toasts.Moving) { _sceneDirty = true; _composeAll |= _toasts.Moving; }
        // A count changing on the bar or in a panel redraws the bar until it has settled.
        if (Zenith.Gui.Motion.CountMotion.Active) { _sceneDirty = true; _barChanged = true; }
        // The snap preview lies outside the top window: compose everything while it shows, and once when it goes.
        bool snap = Windows.SnapPreviewShown;
        if (snap != _snapShown || (snap && Windows.SnapZone != _snapZone)) { _snapShown = snap; _snapZone = Windows.SnapZone; _composeAll = true; _sceneDirty = true; }
        bool watched = _motion.Active || _motionName is not null || Windows.IsDragging || Zenith.Gui.Motion.OverlayMotion.Active || _toasts.Moving || Zenith.Gui.Motion.CountMotion.Active;
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
        var settings = Zenith.Core.Configuration.SystemConfiguration.Settings;
        var desktop = settings.DesktopPreferences;
        var mode = (TaskbarMode)settings.Current.Taskbar;
        var position = (TaskbarPosition)desktop.TaskbarPosition;
        bool moved = position != _taskbar.Position;
        if (mode != _barMode || moved)
        {
            _barMode = mode;
            _taskbar.Position = position;
            Windows.SetWorkArea(TaskbarPolicy.WorkArea(new Rect(0, 0, w, h), mode, position, Theme.TaskbarHeight, Theme.TaskbarMargin));
            _sceneDirty = true;
        }
        if (moved)
        {
            _barPointerUntil = 0;
            _taskbar.ClosePopups();
            _motion.Interrupt();
            ReportMotion();
            _composeAll = true;
        }
        if (_taskbar.SetPins(desktop.TaskbarPins)) { _barChanged = true; _sceneDirty = true; }

        long now = Micros(FrameStats.Now);
        bool near = TaskbarPolicy.PointerNear(position, _input.Y, h, _taskbar.Bounds, _taskbar.Shown, BarRevealRows, Theme.TaskbarMargin);
        if (near && !Windows.IsDragging) { _barPointerUntil = now + BarLingerMicros; }
        Window? top = Windows.Top;
        bool fullscreen = top?.IsFullscreen == true, maximized = top?.IsMaximized == true || Windows.HasTiledWindows;
        bool shown = TaskbarPolicy.Shown(mode, fullscreen, maximized, now < _barPointerUntil, (_prism.IsOpen && _prism.Mode == PrismView.Layout.Menu) || _taskbar.PopupOpen);
        bool docked = TaskbarPolicy.Docked(mode, fullscreen, maximized);
        if (moved) { SetBar(shown, docked); }
        return (shown, docked);
    }

    private void SetBar(bool shown, bool docked)
    {
        _taskbar.Shown = shown;
        _taskbar.Docked = docked;
        if (_prism.IsOpen) { _prism.Place(_taskbar.Bounds, _scene.Width, _scene.Height, _taskbar.Position); }
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
        int away = TaskbarPolicy.SlideAway(_taskbar.Position, Theme.TaskbarHeight + Theme.TaskbarMargin);
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
        if (_lock.IsLocked) { return; }
        if (ApplyWorkspaceTransition()) { return; }
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
        if (_lock.IsLocked) { return -1_000_000 - _lock.HoverKey(x, y); }
        if (_portal.IsOpen) { return -_portal.HoverKey(x, y); }
        if (_sunset.IsOpen) { return -2_000_000 - _sunset.HoverKey(x, y); }
        long key = _prism.IsOpen ? _prism.HoverKey(x, y) : 0;
        if (_toasts.Contains(x, y)) { key ^= 0x5A5A_0000_0000L + y / 8; }
        key = key * 1024 + _taskbar.HoverKey(x, y);
        return key * 4096 + Windows.HoverKey(x, y);
    }

    private Window? KeyEdgesOwner() =>
        !_lock.IsLocked && !_portal.IsOpen && !_sunset.IsOpen && !_prism.IsOpen && !_taskbar.PopupOpen
        && !_switcher.Open && Windows.Focused is { IsClosed: false } focused ? focused : null;

    private bool HandleInput(bool altReleased)
    {
        // The lock screen takes everything: no shortcut, window or taskbar sees input while it is up.
        if (_lock.IsLocked)
        {
            foreach (KeyEvent key in _input.Keys) { _lock.Key(key); }
            if (_input.LeftPressed) { _lock.Click(_input.X, _input.Y); }
            if (_input.Keys.Count > 0 || _input.LeftPressed) { _lockDirty = true; }
            return false;
        }
        // A portal dialog is modal: it takes every key and click until it is answered.
        if (_portal.IsOpen) { _portal.HandleInput(_input); return false; }
        if (_sunset.IsOpen)
        {
            foreach (KeyEvent key in _input.Keys) { if (!_sunset.IsOpen) { break; } _sunset.Key(key, _scene.Width, _scene.Height); }
            if (_sunset.IsOpen && _input.LeftPressed) { _sunset.Click(_input.X, _input.Y, _scene.Width, _scene.Height); }
            return false;
        }
        if (!_prism.IsOpen && HandleWorkspaceInput(altReleased)) { return false; }
        foreach (KeyEvent key in _input.Keys)
        {
            if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == (ConsoleModifiers.Control | ConsoleModifiers.Alt))
            {
                if (key.Key == ConsoleKeyEx.T)
                {
                    OpenApp((key.Modifiers & ConsoleModifiers.Shift) != 0 ? RecoveryTerminal : "Terminal", null);
                    return false;
                }
                if (key.Key == ConsoleKeyEx.L) { Zenith.Core.Sessions.SessionRuntime.RequestLogout(); return false; }
                if (key.Key == ConsoleKeyEx.Delete) { OpenSunset(Zenith.Core.Sessions.SunsetAction.Lock); return false; }
            }
            if (key.Key == ConsoleKeyEx.F11 && Windows.Focused is Window focused) { Windows.ToggleFullscreen(focused); return false; }
            if (key.Key == ConsoleKeyEx.Escape && _taskbar.PopupOpen) { _taskbar.ClosePopups(); return false; }
        }
        // Dragging the sound center's volume knob.
        if (_taskbar.Centers is { Sliding: true } sliding)
        {
            if (_input.Left) { sliding.Slide(_input.X); } else { sliding.StopSliding(); }
            return false;
        }
        // Prism: Alt+Space or Meta opens the palette from anywhere; while open it owns the keyboard
        // and the next click.
        bool prismOwnedKeys = _prism.IsOpen;
        foreach (KeyEvent key in _input.Keys)
        {
            if (key.Key == ConsoleKeyEx.L && (key.Modifiers & ConsoleModifiers.Shift) != 0 && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0
                && _metaAt != 0 && FrameStats.Now - _metaAt < 3 * System.Diagnostics.Stopwatch.Frequency)
            {
                _metaAt = 0; _prism.Close(); OpenSunset(Zenith.Core.Sessions.SunsetAction.Lock); return false;
            }
            if (key.Key is ConsoleKeyEx.LWin or ConsoleKeyEx.RWin) { _metaAt = FrameStats.Now; }
            else { _metaAt = 0; }
            bool palette = key.Key is ConsoleKeyEx.LWin or ConsoleKeyEx.RWin
                || (key.Key == ConsoleKeyEx.Spacebar && (key.Modifiers & ConsoleModifiers.Alt) != 0 && (key.Modifiers & ConsoleModifiers.Control) == 0);
            if (palette)
            {
                prismOwnedKeys = true;
                if (_prism.IsOpen && _prism.Mode == PrismView.Layout.Palette) { _prism.Close(); }
                else { _prism.Open(PrismView.Layout.Palette, _taskbar.Bounds, _scene.Width, _scene.Height, _taskbar.Position); }
                // Keys typed with or right after Meta go on: Shift+L may complete Meta+Shift+L, others search.
                continue;
            }
            if (_prism.IsOpen) { _prism.HandleKey(key); }
        }
        bool menuWasOpen = _prism.IsOpen;
        if (menuWasOpen)
        {
            // Prism's list scrolls with the wheel and its scroll bar drags.
            if (_input.Scroll != 0) { _prism.Wheel(_input.X, _input.Y, _input.Scroll); }
            if (_prism.Dragging) { if (_input.Left) { _prism.Drag(_input.Y); } else { _prism.Release(); } }
            if (!_input.LeftPressed) { return false; }
            _prism.HandleClick(_input.X, _input.Y);
            if (!_taskbar.Bounds.Contains(_input.X, _input.Y)) { return false; }
        }

        // A click on a toast dismisses it and goes no further.
        if (_input.LeftPressed && _toasts.HandleClick(_input.X, _input.Y)) { _sceneDirty = true; return false; }
        if (_input.LeftPressed && _taskbar.HandleClick(_input.X, _input.Y, out bool launcherClicked))
        {
            if (launcherClicked)
            {
                if (menuWasOpen) { _prism.Close(); }
                else
                {
                    var layout = Zenith.Core.Configuration.SystemConfiguration.Settings.Current.Launcher == 1 ? PrismView.Layout.Palette : PrismView.Layout.Menu;
                    _prism.Open(layout, _taskbar.Bounds, _scene.Width, _scene.Height, _taskbar.Position);
                }
            }

            return false;
        }

        // The wheel over the sound button changes the volume.
        if (_input.Scroll != 0 && _taskbar.ScrollOverflow(_input.X, _input.Y, _input.Scroll)) { return false; }
        if (_input.Scroll != 0 && _taskbar.Shown && _taskbar.Centers is TaskbarCenters centers && centers.Scroll(_input.X, _input.Y, _input.Scroll)) { return false; }
        // The key that accepted a launcher result still belongs to Prism, even if the popup closed.
        if (prismOwnedKeys) { return false; }
        Windows.HandleInput(_input);
        return true;
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
        if (_lock.IsLocked) { ComposeLocked(start); return; }
        bool taskbarLabel = !_prism.IsOpen && !_switcher.Open && !_portal.IsOpen && !_sunset.IsOpen && _taskbar.HasHoverLabel(_input.X, _input.Y);
        bool overlays = withoutTop || Windows.SnapPreviewShown || _toasts.IsShown || _prism.IsOpen || _switcher.Open || _portal.IsOpen || _sunset.IsOpen || _taskbar.PopupOpen || taskbarLabel || Zenith.Core.Sessions.SessionRuntime.Message is not null;
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
        if (_toasts.IsShown) { _toasts.Draw(_scene, _input.X, _input.Y); }
        if (_prism.IsOpen) { _prism.Draw(_scene, _input.X, _input.Y); }
        if (_taskbar.Shown) { _taskbar.DrawOverflowList(_scene, _input.X, _input.Y); }
        if (taskbarLabel) { _taskbar.DrawHoverLabel(_scene, _input.X, _input.Y); }
        if (_sunset.IsOpen && !_sunsetCardHidden) { _sunset.Draw(_scene, _input.X, _input.Y); }
        else if (_sunset.IsOpen) { _scene.FillRect(_scene.Bounds, 0x99000000U); }
        if (_switcher.Open) { _switcher.Draw(_scene, Windows); }
        if (_portal.IsOpen) { _portal.Draw(_scene, _input.X, _input.Y); }

        FrameStats.ShellTicks += FrameStats.Now - shell;
        _scene.BaseClip = _scene.Bounds; MaterialComposition.Limit = MaterialComposition.Unlimited;
        var policy = MaterialResources.Policy;
        Zenith.Core.Configuration.SystemConfiguration.Appearance.ReportMaterials(7, policy.Effective, policy.Reason);
        FrameStats.RecordCompose(start);
    }
    private int WidthForMessage() => 520;

    private bool _lockDirty = true, _sunsetCardHidden;
    /// <summary>While locked only the lock screen is drawn, and only when it changed: windows that keep drawing
    /// behind it cost nothing on screen.</summary>
    private void ComposeLocked(long start)
    {
        _composeAll = true;
        if (!_lockDirty && _lastLocked) { _composeDamage = new Rect(0, 0, 0, 0); FrameStats.RecordCompose(start); return; }
        _lockDirty = false; _lastLocked = true;
        _lock.Draw(_scene, _wallpaper, _input.X, _input.Y);
        _composeDamage = _scene.Bounds;
        FrameStats.RecordCompose(start);
    }
    private bool _lastLocked;

    // Sunset (docs/components/SUNSET.md).
    public void OpenSunset(Zenith.Core.Sessions.SunsetAction preferred)
    {
        if (_lock.IsLocked || _portal.IsOpen) { return; }
        _prism.Close();
        _sunset.Open(preferred, _scene.Width, _scene.Height);
        _sceneDirty = true; _composeAll = true;
        // Fades and scales in over the dimmed desktop; Animations Off opens it at once.
        int percent = Zenith.Core.Configuration.SystemConfiguration.Settings.AnimationPercent;
        _motion.Interrupt(); ReportMotion();
        if (percent == 0 || !MotionMemory()) { return; }
        _sunsetCardHidden = true; ComposeScene(); CopyBase();
        _sunsetCardHidden = false; ComposeScene();
        Rect area = _sunset.Card.Inflate(Theme.ShadowSize).Intersect(_scene.Bounds);
        _motionLayer.Capture(_scene.Pixels, _scene.Width, _scene.Height, area);
        Rect from = new(area.X + area.W * 3 / 100, area.Y + area.H * 3 / 100, area.W - area.W * 6 / 100, area.H - area.H * 6 / 100);
        _motion.Begin(_motionBase!, _motionLayer, from, area, 0, 255, Micros(FrameStats.Now), SunsetOpenMicros * percent / 100);
        _motionName = "SunsetOpened";
    }

    string Zenith.Gui.Shell.Sunset.ISunsetHost.Who => SessionName();
    private static string SessionName() => Accounts.Service?.Name(Accounts.Session.Uid) ?? (Accounts.Session.IsRoot ? "root" : "user");

    string? Zenith.Gui.Shell.Sunset.ISunsetHost.LockUnavailable
    {
        get
        {
            var service = Accounts.Service;
            if (service is null) { return "accounts are unavailable."; }
            var account = service.Find(SessionName());
            return account is null || account.Password is "" or "!" or "*" ? "set a password for " + SessionName() + " to lock the screen." : null;
        }
    }
    string? Zenith.Gui.Shell.Sunset.ISunsetHost.LogoutUnavailable
        => Accounts.Service is null || !Accounts.Service.HasPasswords ? "set up accounts before logging out." : null;
    string? Zenith.Gui.Shell.Sunset.ISunsetHost.PowerUnavailable => null;

    /// <summary>What a log out, restart or shut down would lose or interrupt now, with the window that can
    /// deal with each. The same checks the session makes before it ends.</summary>
    public System.Collections.Generic.List<Zenith.Core.Sessions.SessionBlocker> Blockers()
    {
        var list = new System.Collections.Generic.List<Zenith.Core.Sessions.SessionBlocker>();
        var busyPids = _nativeClients.BusyTerminalOwners();
        bool terminal = false;
        foreach (var window in Windows.Windows)
        {
            if (window.IsClosed) { continue; }
            string owner = window.Title.TrimEnd(' ', '\u2022');
            if (window.HasUnsavedChanges) { list.Add(new(owner, "has unsaved changes", true, window)); }
            if (window is TerminalWindow t && (t.IsBusy || t.HasBackgroundJobs))
            { list.Add(new(owner, "a command is still running", false, window)); terminal = true; }
            else if (window.OwnerPid != 0 && busyPids.Remove(window.OwnerPid))
            { list.Add(new(owner, "a command is still running", false, window)); terminal = true; }
        }
        if (busyPids.Count > 0) { list.Add(new("Terminal", "a command is still running", false, null)); terminal = true; }
        if (Zenith.Core.Storage.Installer.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning)
        { list.Add(new("System", "an installation or update is running", false, null)); }
        else if (!terminal && Zenith.Core.Processes.NativeProcess.IsRunning)
        { list.Add(new("Programs", "a program without a window is still running", false, null)); }
        return list;
    }

    private void LockDesktop(long graceUntil = 0)
    {
        _motion.Interrupt(); ReportMotion(); _prism.Close(); _sunset.Close(); _taskbar.ClosePopups(); CancelSwitcher();
        Windows.CancelPointerCapture(); _keyEdgesOwner?.ResetKeyEdges();
        _lock.Lock(SessionName(), Accounts.Session.Uid, graceUntil);
        _lockDirty = _sceneDirty = _composeAll = true; _lastLocked = false;
    }

    string? Zenith.Gui.Shell.Sunset.ISunsetHost.Act(Zenith.Core.Sessions.SunsetAction action)
    {
        _sceneDirty = true; _composeAll = true;
        switch (action)
        {
            case Zenith.Core.Sessions.SunsetAction.Lock:
                LockDesktop();
                return null;
            case Zenith.Core.Sessions.SunsetAction.LogOut:
                return Zenith.Core.Sessions.SessionRuntime.RequestLogout();
            default:
                if (Blockers().Count > 0) { return "something started meanwhile."; }
                if (Zenith.Core.PowerControl.BlockedReason is string blocked) { return blocked; }
                // One frame without the popup first, so the shutdown fade starts from the desktop.
                _pendingPower = action; _pendingPowerFrame = FrameStats.Frames;
                return null;
        }
    }

    void Zenith.Gui.Shell.Sunset.ISunsetHost.Show(object target)
    {
        if (target is Window window && !window.IsClosed) { Windows.Activate(window); _sceneDirty = true; }
    }

    private void RunPendingPower()
    {
        if (_pendingPower is not Zenith.Core.Sessions.SunsetAction action || FrameStats.Frames <= _pendingPowerFrame) { return; }
        _pendingPower = null;
        // The person at the console chose this in the desktop's own popup with nothing in the way; the greeter
        // offers the same to anyone. Unmounting and powering off are root operations, so the desktop performs
        // them as root (released explicitly: Cosmos skips finally blocks while unwinding).
        var root = CredentialContext.Enter(Credentials.Root);
        try
        {
            Zenith.Core.Log.Write("sunset", action == Zenith.Core.Sessions.SunsetAction.Restart ? "restart" : "shut down");
            if (action == Zenith.Core.Sessions.SunsetAction.Restart) { Zenith.Core.PowerControl.Reboot(); } else { Zenith.Core.PowerControl.PowerOff(); }
        }
        catch (System.Exception e)
        {
            root.Dispose();
            Zenith.Core.Log.Write("sunset", "power change failed: " + e.Message);
            OpenSunset(action);
            return;
        }
        root.Dispose();
    }

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
        if (_lock.IsLocked || _sunset.IsOpen || _prism.IsOpen || _portal.IsOpen || _toasts.Contains(_input.X, _input.Y) || (_taskbar.Shown && _taskbar.Bounds.Contains(_input.X, _input.Y))) { return CursorShape.Default; }
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
    private long _cursorKey = -1, _styleKey = -1;
    private bool _portalSounded;
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
        if (_taskbar.Centers is TaskbarCenters centers) { hash.Add(centers.StateKey()); }
        hash.Add(Windows.DesktopCount); hash.Add(Windows.ActiveDesktopIndex);
        foreach (Window w in Windows.Windows)
        {
            if (!Windows.IsOnActiveDesktop(w)) { continue; }
            hash.Add(w.Title); hash.Add(w.IsFocused); hash.Add(w.IsMinimized);
        }
        if (_taskbar.Bounds.Inflate(Theme.TaskbarMargin).Contains(_input.X, _input.Y)) { hash.Add(_input.X); hash.Add(_input.Y); }
        return hash.ToHashCode();
    }
}
