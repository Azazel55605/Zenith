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

internal sealed record SystemSettings(string Keyboard, string TimeZone, SessionMode DefaultSession, uint Animation = 0, uint Taskbar = 0);

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
    public static readonly string[] TaskbarNames = { "always", "maximized", "auto" };
    public static readonly string[] TaskbarLabels = { "Always visible", "Hide when maximized", "Auto-hide" };
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
            string keymap = "us", zone = "UTC"; SessionMode session = SessionMode.Desktop; uint animation = 0, taskbar = 0;
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
            _keyboard(keymap); _zone(zone);
            Current = new(keymap, zone, session, animation, taskbar);
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
    public IReadOnlyList<SessionMode> SessionChoices
    { get { var result = new List<SessionMode>(); foreach(var mode in new[] { SessionMode.Desktop, SessionMode.Headless, SessionMode.WindowManager }) { if(_available(mode)) { result.Add(mode); } } return result; } }
    public int SaveChoice(uint field, uint choice, ulong revision)
    {
        if (!CredentialContext.Current.IsRoot) { return -13; }
        if (field is not (1 or 2 or 3 or 4 or 5) || (field == 1 ? choice >= KeyboardLayoutCatalog.Names.Length : field == 2 ? choice >= TimeZones.All.Count : field == 3 ? choice >= SessionChoices.Count : field == 4 ? choice >= AnimationNames.Length : choice >= TaskbarNames.Length)) { return -22; }
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
                else { SetTaskbar(TaskbarNames[choice]); }
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
