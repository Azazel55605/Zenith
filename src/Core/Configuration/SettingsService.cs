using System;
using System.Collections.Generic;
using System.Text;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Core.Time;

namespace Zenith.Core.Configuration;

internal interface ISettingsStore
{
    string? Read(string path);
    void Replace(string path, string text);
    bool Persistent { get; }
}

internal sealed record SystemSettings(string Keyboard, string TimeZone, SessionMode DefaultSession, uint Animation = 0, uint Taskbar = 0,
    uint Wallpaper = 0, uint Launcher = 0, uint Cursor = 0, uint Windows = 0);

/// <summary>Shared validated settings operations. Local /etc state is administrator-owned.</summary>
internal sealed class SettingsService
{
    public const int MaximumBytes = 16384;
    public const string KeyboardPath = "/etc/vconsole.conf";
    public const string TimeZonePath = "/etc/timezone";
    public const string SessionPath = "/etc/zenith/session.conf";
    public const string MotionPath = "/etc/zenith/motion.conf";
    public static readonly string[] AnimationNames = { "normal", "fast", "slow", "off" };
    public static readonly string[] AnimationLabels = { "Normal", "Fast", "Slow", "Off (reduced motion)" };
    private static readonly int[] AnimationPercents = { 100, 60, 150, 0 };
    /// <summary>Animation durations as a percentage of normal; 0 means window changes are immediate.</summary>
    public int AnimationPercent => AnimationPercents[Current.Animation];
    public const string TaskbarPath = "/etc/zenith/taskbar.conf";
    public static readonly string[] TaskbarNames = { "dock-maximized", "floating", "docked", "maximized", "auto" };
    public static readonly string[] TaskbarLabels = { "Dock when maximized", "Floating", "Docked", "Hide when maximized", "Auto-hide" };
    public const string WallpaperPath = "/etc/zenith/wallpaper.conf";
    /// <summary>Dynamic wallpapers are formulas the compositor draws for the current mode and accent;
    /// solid ones are a plain fill ("solid-accent" follows the accent). At most 12 (ABI).</summary>
    /// <summary>The first 12 fit the legacy snapshot (call 625); the second set (2026-11-19) follows, read through
    /// the style catalogue (call 669).</summary>
    public static readonly string[] WallpaperNames = { "zenith", "dunes", "aurora", "contour", "solid-graphite", "solid-midnight",
        "solid-forest", "solid-plum", "solid-cream", "solid-mist", "solid-sand", "solid-accent", "orbits", "tides", "facets", "nebula" };
    public static readonly string[] WallpaperLabels = { "Zenith (dynamic)", "Dunes (dynamic)", "Aurora (dynamic)", "Contour (dynamic)",
        "Graphite", "Midnight", "Forest", "Plum", "Cream", "Mist", "Sand", "Accent color", "Orbits (dynamic)", "Tides (dynamic)", "Facets (dynamic)", "Nebula (dynamic)" };
    public const string CursorPath = "/etc/zenith/cursor.conf";
    /// <summary>The Meridian cursor set's variants (docs/design/cursor-concept.svg).</summary>
    public static readonly string[] CursorNames = { "light", "dark", "contrast-white", "contrast-black", "comet" };
    public static readonly string[] CursorLabels = { "Meridian Light", "Meridian Dark", "Contrast White", "Contrast Black", "Comet" };
    public const string LauncherPath = "/etc/zenith/launcher.conf";
    /// <summary>How resizable native windows open: scaled to the screen, or at the size the program asks for.</summary>
    public const string WindowsPath = "/etc/zenith/windows.conf";
    public static readonly string[] WindowsNames = { "fit", "compact" };
    public static readonly string[] WindowsLabels = { "Sized to the screen", "Compact (the program's own size)" };
    public static readonly string[] LauncherNames = { "menu", "palette" };
    public static readonly string[] LauncherLabels = { "Start menu", "Search palette" };
    private readonly ISettingsStore _store;
    private readonly Action<string> _keyboard;
    private readonly Action<string> _zone;
    private readonly Func<SessionMode, bool> _available;
    private readonly object _gate = new();
    public SystemSettings Current { get; private set; } = new("us", "UTC", SessionMode.Desktop);
    private ulong _revision = 1;
    public (SystemSettings Settings, ulong Revision) Snapshot() { lock (_gate) { return (Current, _revision); } }
    public bool Persistent => _store.Persistent;

    public SettingsService(ISettingsStore store, Action<string> keyboard, Action<string> zone, Func<SessionMode, bool> available)
    { _store = store; _keyboard = keyboard; _zone = zone; _available = available; }

    public IReadOnlyList<string> Load()
    {
        lock (_gate)
        {
            var errors = new List<string>();
            string keymap = "us", zone = "UTC"; SessionMode session = SessionMode.Desktop; uint animation = 0, taskbar = 0, wallpaper = 0, launcher = 0, cursor = 0, windows = 0;
            try { var fields = Fields(_store.Read(KeyboardPath)); if (fields.TryGetValue("KEYMAP", out string? value)) { keymap = KeyboardLayoutCatalog.Normalize(value) ?? throw new FormatException("unknown KEYMAP"); } }
            catch (Exception e) { errors.Add(KeyboardPath + ": " + e.Message + "; using us"); }
            try { string value = Bound(_store.Read(TimeZonePath) ?? "UTC").Trim(); if (value.Length > 0) { zone = NormalizeZone(value); } }
            catch (Exception e) { errors.Add(TimeZonePath + ": " + e.Message + "; using UTC"); }
            try { session = ReadSession(_store.Read(SessionPath)); }
            catch (Exception e) { errors.Add(SessionPath + ": " + e.Message + "; using de"); }
            try { animation = ReadAnimation(_store.Read(MotionPath)); }
            catch (Exception e) { errors.Add(MotionPath + ": " + e.Message + "; using normal"); }
            try { taskbar = ReadTaskbar(_store.Read(TaskbarPath)); }
            catch (Exception e) { errors.Add(TaskbarPath + ": " + e.Message + "; using always"); }
            try { wallpaper = ReadChoice(_store.Read(WallpaperPath), "WALLPAPER", WallpaperNames); }
            catch (Exception e) { errors.Add(WallpaperPath + ": " + e.Message + "; using zenith"); }
            try { launcher = ReadChoice(_store.Read(LauncherPath), "LAUNCHER", LauncherNames); }
            catch (Exception e) { errors.Add(LauncherPath + ": " + e.Message + "; using menu"); }
            try { cursor = ReadChoice(_store.Read(CursorPath), "CURSOR", CursorNames); }
            catch (Exception e) { errors.Add(CursorPath + ": " + e.Message + "; using light"); }
            try { windows = ReadChoice(_store.Read(WindowsPath), "WINDOWS", WindowsNames); }
            catch (Exception e) { errors.Add(WindowsPath + ": " + e.Message + "; using fit"); }
            LoadStyles(errors);
            LoadSounds(errors);
            _keyboard(keymap); _zone(zone);
            Current = new(keymap, zone, session, animation, taskbar, wallpaper, launcher, cursor, windows);
            return errors;
        }
    }

    public void SetKeyboard(string name)
    {
        RequireRoot();
        string canonical = KeyboardLayoutCatalog.Normalize(name) ?? throw new ArgumentException("unknown keyboard layout");
        lock (_gate)
        {
            if (_revision == ulong.MaxValue) { throw new InvalidOperationException("settings revision exhausted"); }
            string text = Update(_store.Read(KeyboardPath), new Dictionary<string, string> { ["KEYMAP"] = canonical });
            _store.Replace(KeyboardPath, text); _keyboard(canonical);
            Current = Current with { Keyboard = canonical }; _revision++;
        }
    }
    public void SetTimeZone(string name)
    {
        RequireRoot(); string canonical = NormalizeZone(name);
        lock (_gate)
        {
            if (_revision == ulong.MaxValue) { throw new InvalidOperationException("settings revision exhausted"); }
            _store.Replace(TimeZonePath, canonical + "\n"); _zone(canonical);
            Current = Current with { TimeZone = canonical }; _revision++;
        }
    }
    public void SetSession(SessionMode mode)
    {
        RequireRoot();
        if (!_available(mode)) { throw new InvalidOperationException("session '" + SessionModes.Name(mode) + "' is unavailable in this build"); }
        lock (_gate)
        {
            if (_revision == ulong.MaxValue) { throw new InvalidOperationException("settings revision exhausted"); }
            string? old = _store.Read(SessionPath);
            _ = ReadSession(old); // Never silently downgrade an unknown future schema.
            _store.Replace(SessionPath, Update(old, new Dictionary<string, string> { ["VERSION"] = "1", ["SESSION"] = SessionModes.Name(mode) }));
            Current = Current with { DefaultSession = mode }; _revision++;
        }
    }
    public void SetAnimation(string name)
    {
        RequireRoot();
        int choice = Array.IndexOf(AnimationNames, name);
        if (choice < 0) { throw new ArgumentException("unknown animation setting"); }
        lock (_gate)
        {
            if (_revision == ulong.MaxValue) { throw new InvalidOperationException("settings revision exhausted"); }
            string? old = _store.Read(MotionPath);
            _ = ReadAnimation(old); // Never silently downgrade an unknown future schema.
            _store.Replace(MotionPath, Update(old, new Dictionary<string, string> { ["VERSION"] = "1", ["ANIMATION"] = name }));
            Current = Current with { Animation = (uint)choice }; _revision++;
        }
    }
    public void SetTaskbar(string name)
    {
        RequireRoot();
        int choice = Array.IndexOf(TaskbarNames, name);
        if (choice < 0) { throw new ArgumentException("unknown taskbar setting"); }
        lock (_gate)
        {
            if (_revision == ulong.MaxValue) { throw new InvalidOperationException("settings revision exhausted"); }
            string? old = _store.Read(TaskbarPath);
            _ = ReadTaskbar(old); // Never silently downgrade an unknown future schema.
            _store.Replace(TaskbarPath, Update(old, new Dictionary<string, string> { ["VERSION"] = "1", ["TASKBAR"] = name }));
            Current = Current with { Taskbar = (uint)choice }; _revision++;
        }
    }
    public void SetWallpaper(string name) => SetChoice(WallpaperPath, "WALLPAPER", WallpaperNames, name, c => Current = Current with { Wallpaper = c });
    public void SetLauncher(string name) => SetChoice(LauncherPath, "LAUNCHER", LauncherNames, name, c => Current = Current with { Launcher = c });
    public void SetWindows(string name) => SetChoice(WindowsPath, "WINDOWS", WindowsNames, name, c => Current = Current with { Windows = c });
    public void SetCursor(string name) => SetChoice(CursorPath, "CURSOR", CursorNames, name, c => Current = Current with { Cursor = c });
    private void SetChoice(string path, string key, string[] names, string name, Action<uint> apply)
    {
        RequireRoot();
        int choice = Array.IndexOf(names, name);
        if (choice < 0) { throw new ArgumentException("unknown " + key.ToLowerInvariant() + " setting"); }
        lock (_gate)
        {
            if (_revision == ulong.MaxValue) { throw new InvalidOperationException("settings revision exhausted"); }
            string? old = _store.Read(path);
            _ = ReadChoice(old, key, names); // Never silently downgrade an unknown future schema.
            _store.Replace(path, Update(old, new Dictionary<string, string> { ["VERSION"] = "1", [key] = name }));
            apply((uint)choice); _revision++;
        }
    }
    private static uint ReadChoice(string? text, string key, string[] names)
    {
        var fields = Fields(text);
        if (fields.TryGetValue("VERSION", out string? version) && version != "1") { throw new FormatException("unsupported settings VERSION"); }
        if (!fields.TryGetValue(key, out string? name)) { return 0; }
        int choice = Array.IndexOf(names, name);
        return choice >= 0 ? (uint)choice : throw new FormatException("unknown " + key);
    }
    // ---------- Style choices (U13, 2026-11-19): one file, one key each, fields 9..16 of the save call.
    public const string StylePath = "/etc/zenith/style.conf";
    public sealed record StyleChoice(uint Field, string Key, string[] Names, string[] Labels);
    /// <summary>Filled in as each style lands (decorations, widgets, buttons, splash, login, boot, sounds, cursor size).</summary>
    public static readonly StyleChoice[] Styles =
    {
        new(9, "CURSOR_SIZE", new[] { "24", "32", "48" }, new[] { "Normal (24 px)", "Large (32 px)", "Largest (48 px)" }),
        new(10, "DECORATION", new[] { "zenith", "classic", "dots", "ribbon" }, new[] { "Zenith", "Classic", "Dots", "Ribbon" }),
        new(11, "WIDGETS", new[] { "rounded", "soft", "sharp" }, new[] { "Rounded", "Soft", "Sharp" }),
        new(12, "BUTTONS", new[] { "filled", "tonal", "outline" }, new[] { "Filled", "Tonal", "Outline" }),
        new(13, "SPLASH", new[] { "crossfade", "rise", "iris", "greeting" }, new[] { "Crossfade", "Rise", "Iris", "Greeting" }),
        new(14, "LOGIN", new[] { "card", "accounts", "clock", "split" }, new[] { "Zenith card", "Accounts", "Clock", "Split" }),
        new(15, "BOOT", new[] { "phases", "orbit", "pulse", "quiet" }, new[] { "Phases", "Orbit", "Pulse", "Quiet" }),
        new(16, "SOUNDS", new[] { "chimes", "silent" }, new[] { "Zenith Chimes", "Silent" }),
        new(17, "ICONS", new[] { "zenith", "bold", "fine" }, new[] { "Zenith", "Zenith Bold", "Zenith Fine" }),
    };

    private readonly uint[] _styles = new uint[Styles.Length];

    // ---------- System sound level and events (U14): /etc/zenith/sounds.conf, LEVEL=0..100, OFF=event,event.
    public const string SoundsPath = "/etc/zenith/sounds.conf";
    public static readonly string[] SoundEvents = { "login", "logout", "notification", "attention", "error", "device-added", "device-removed", "done", "volume", "trash", "screenshot", "shutdown" };
    public static readonly string[] SoundEventLabels = { "Sign in", "Sign out and lock", "Notification", "Attention", "Error", "Device connected", "Device removed", "Done", "Volume", "Trash emptied", "Screenshot", "Shut down" };
    public const uint DefaultSoundMask = 0xFFFu & ~(1u << 9) & ~(1u << 10);   // trash and screenshot off
    public uint SoundLevel { get; private set; } = 70;
    public uint SoundMask { get; private set; } = DefaultSoundMask;
    private void LoadSounds(List<string> errors)
    {
        try
        {
            var fields = Fields(_store.Read(SoundsPath));
            if (fields.TryGetValue("LEVEL", out string? level)) { SoundLevel = uint.TryParse(level, out uint v) && v <= 100 ? v : throw new FormatException("bad LEVEL"); }
            if (fields.TryGetValue("OFF", out string? off))
            {
                uint mask = 0xFFF;
                foreach (string name in off.Split(',', StringSplitOptions.RemoveEmptyEntries))
                { int i = Array.IndexOf(SoundEvents, name.Trim()); if (i >= 0) { mask &= ~(1u << i); } }
                SoundMask = mask;
            }
        }
        catch (Exception e) { SoundLevel = 70; SoundMask = DefaultSoundMask; errors.Add(SoundsPath + ": " + e.Message + "; using defaults"); }
    }
    public int SaveSounds(uint level, uint mask, ulong revision)
    {
        if (!CredentialContext.Current.IsRoot) { return -13; }
        if (level > 100 || mask > 0xFFF) { return -22; }
        lock (_gate)
        {
            if (revision != _revision) { return -11; }
            var off = new List<string>();
            for (int i = 0; i < SoundEvents.Length; i++) { if ((mask & (1u << i)) == 0) { off.Add(SoundEvents[i]); } }
            try { _store.Replace(SoundsPath, Update(_store.Read(SoundsPath), new Dictionary<string, string> { ["VERSION"] = "1", ["LEVEL"] = level.ToString(), ["OFF"] = string.Join(",", off) })); }
            catch { return -5; }
            SoundLevel = level; SoundMask = mask; _revision++;
            return 0;
        }
    }
    public static StyleChoice? StyleFor(uint field) { foreach (var s in Styles) { if (s.Field == field) { return s; } } return null; }
    /// <summary>The current choice of a style field (9..16).</summary>
    public uint Style(uint field) { lock (_gate) { for (int i = 0; i < Styles.Length; i++) { if (Styles[i].Field == field) { return _styles[i]; } } return 0; } }
    public void SetStyle(uint field, string name)
    {
        var style = StyleFor(field) ?? throw new ArgumentException("unknown style field");
        int index = Array.IndexOf(Styles, style);
        SetChoice(StylePath, style.Key, style.Names, name, c => _styles[index] = c);
    }
    private void LoadStyles(List<string> errors)
    {
        string? text = null;
        try { text = _store.Read(StylePath); } catch (Exception e) { errors.Add(StylePath + ": " + e.Message); }
        for (int i = 0; i < Styles.Length; i++)
        {
            try { _styles[i] = ReadChoice(text, Styles[i].Key, Styles[i].Names); }
            catch (Exception e) { _styles[i] = 0; errors.Add(StylePath + " " + Styles[i].Key + ": " + e.Message + "; using " + Styles[i].Names[0]); }
        }
    }
    public IReadOnlyList<SessionMode> SessionChoices
    { get { var result = new List<SessionMode>(); foreach(var mode in new[] { SessionMode.Desktop, SessionMode.Headless, SessionMode.WindowManager }) { if(_available(mode)) { result.Add(mode); } } return result; } }
    public int SaveChoice(uint field, uint choice, ulong revision)
    {
        if (!CredentialContext.Current.IsRoot) { return -13; }
        if (StyleFor(field) is StyleChoice style)
        {
            if (choice >= style.Names.Length) { return -22; }
            lock (_gate)
            {
                if (revision != _revision) { return -11; }
                if (_revision == ulong.MaxValue) { return -75; }
                try { SetStyle(field, style.Names[choice]); return 0; } catch { return -5; }
            }
        }
        if (field is < 1 or > 8 || (field == 1 ? choice >= KeyboardLayoutCatalog.Names.Length : field == 2 ? choice >= TimeZones.All.Count : field == 3 ? choice >= SessionChoices.Count
            : field == 4 ? choice >= AnimationNames.Length : field == 5 ? choice >= TaskbarNames.Length : field == 6 ? choice >= WallpaperNames.Length : field == 7 ? choice >= LauncherNames.Length : choice >= CursorNames.Length)) { return -22; }
        lock (_gate)
        {
            if (revision != _revision) { return -11; }
            if (_revision == ulong.MaxValue) { return -75; }
            try
            {
                if (field == 1) { SetKeyboard(KeyboardLayoutCatalog.Names[choice]); }
                else if(field == 2) { SetTimeZone(TimeZones.All[(int)choice].Name); }
                else if(field == 3) { SetSession(SessionChoices[(int)choice]); }
                else if(field == 4) { SetAnimation(AnimationNames[choice]); }
                else if(field == 5) { SetTaskbar(TaskbarNames[choice]); }
                else if(field == 6) { SetWallpaper(WallpaperNames[choice]); }
                else if(field == 7) { SetLauncher(LauncherNames[choice]); }
                else { SetCursor(CursorNames[choice]); }
                return 0;
            }
            catch { return -5; }
        }
    }
    private static void RequireRoot()
    { if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("system settings require root"); } }
    private static string NormalizeZone(string name) => TimeZones.TryFind(name, out var zone) ? zone.Name : throw new ArgumentException("unknown time zone");
    private static SessionMode ReadSession(string? text)
    {
        var fields = Fields(text);
        // Missing version is the legacy v0 representation; saves migrate it to v1.
        if (fields.TryGetValue("VERSION", out string? version) && version != "1") { throw new FormatException("unsupported settings VERSION"); }
        if (!fields.TryGetValue("SESSION", out string? name)) { return SessionMode.Desktop; }
        if (!SessionModes.TryParse(name, out var mode)) { throw new FormatException("unknown SESSION"); }
        return mode;
    }
    private static uint ReadAnimation(string? text)
    {
        var fields = Fields(text);
        if (fields.TryGetValue("VERSION", out string? version) && version != "1") { throw new FormatException("unsupported settings VERSION"); }
        if (!fields.TryGetValue("ANIMATION", out string? name)) { return 0; }
        int choice = Array.IndexOf(AnimationNames, name);
        return choice >= 0 ? (uint)choice : throw new FormatException("unknown ANIMATION");
    }
    private static uint ReadTaskbar(string? text)
    {
        var fields = Fields(text);
        if (fields.TryGetValue("VERSION", out string? version) && version != "1") { throw new FormatException("unsupported settings VERSION"); }
        if (!fields.TryGetValue("TASKBAR", out string? name)) { return 0; }
        if (name == "always") { name = "floating"; } // The name 0.0.35 saved.
        int choice = Array.IndexOf(TaskbarNames, name);
        return choice >= 0 ? (uint)choice : throw new FormatException("unknown TASKBAR");
    }
    private static string Bound(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes || text.IndexOf('\0') >= 0) { throw new FormatException("invalid or oversized configuration"); }
        return text.Replace("\r\n", "\n");
    }
    private static Dictionary<string, string> Fields(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in Bound(text ?? "").Split('\n'))
        {
            string line = raw.Trim(); if (line.Length == 0 || line[0] == '#') { continue; }
            int split = line.IndexOf('='); if (split < 1) { throw new FormatException("expected KEY=value"); }
            string key = line.Substring(0, split).Trim(), value = line.Substring(split + 1).Trim();
            if (value.StartsWith('"'))
            { if (value.Length < 2 || !value.EndsWith('"')) { throw new FormatException("unclosed configuration quote"); } value = value.Substring(1, value.Length - 2); }
            if (!result.TryAdd(key, value)) { throw new FormatException("duplicate configuration key " + key); }
        }
        return result;
    }
    private static string Update(string? old, Dictionary<string, string> replacements)
    {
        var lines = new List<string>();
        foreach (string line in Bound(old ?? "").Split('\n'))
        {
            int split = line.IndexOf('=');
            if (line.Length > 0 && (split < 0 || !replacements.ContainsKey(line.Substring(0, split).Trim()))) { lines.Add(line); }
        }
        foreach (var pair in replacements) { lines.Add(pair.Key + "=" + pair.Value); }
        return Bound(string.Join("\n", lines) + "\n");
    }
}
