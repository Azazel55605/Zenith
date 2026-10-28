using System;
using System.Collections.Generic;
using System.Text;
using Zenith.Core.Security;

namespace Zenith.Core.Configuration;

internal sealed record AppearanceSelection(uint Mode, uint Accent, ulong Revision, uint Materials = 0, uint Translucency = 40);
internal sealed record AppearanceState(AppearanceSelection Saved, AppearanceSelection Active, AppearancePalette Palette, ulong Resources);

/// <summary>Shared system preference authority. Never exposes paths or invokes shell commands.</summary>
internal sealed class AppearanceService
{
    public const string Path = "/etc/zenith/appearance.conf";
    private readonly ISettingsStore _store;
    private readonly object _gate = new();
    private volatile AppearanceSelection _saved = new(0, 0, 1);
    private volatile AppearanceSelection _active = new(0, 0, 1);
    private volatile AppearancePalette _palette = AppearancePalette.Default;
    private ulong _resources = 1;
    private volatile bool _explicit;
    private uint _materialAvailable, _materialEffective, _materialReason = 2;
    public (uint Available, uint Effective, uint Reason) MaterialStatus
    { get { lock (_gate) { return (_materialAvailable, _materialEffective, _materialReason); } } }
    public void ReportMaterials(uint available, uint effective, uint reason)
    { lock (_gate) { _materialAvailable = available & 7; _materialEffective = effective & available & 7; _materialReason = reason; } }
    public bool UsePalette => _explicit || _palette.Packaged;
    public AppearancePalette Palette => _palette;
    public bool Persistent => _store.Persistent;
    public AppearanceSelection Saved => _saved;
    public AppearanceSelection Active => _active;
    public AppearanceService(ISettingsStore store) { _store = store; }
    public string? Load()
    {
        lock (_gate)
        {
            try
            {
                string? text = _store.Read(Path); var value = Parse(text);
                _explicit = text is not null;
                _saved = _active = new(value.Mode, value.Accent, 1, value.Materials, value.Translucency);
                return null;
            }
            catch (Exception e) { _saved = _active = new(0, 0, 1); return e.Message + "; using dark/blue"; }
        }
    }
    // Result codes are stable service errors, not exception text or authority supplied by a client.
    public int Save(uint mode, uint accent, ulong expectedRevision)
    { lock (_gate) { return SaveFull(mode, accent, _saved.Materials, expectedRevision); } }
    public int SaveSurfaceMaterials(uint mode, uint accent, uint materials, ulong revision)
    {
        if (!CredentialContext.Current.IsRoot) { return -13; }
        if (materials > 3) { return -22; }
        lock (_gate) { return SaveFull(mode, accent, materials | (_saved.Materials & 4), revision); }
    }
    public int SaveFull(uint mode, uint accent, uint materials, ulong expectedRevision, uint? translucency = null)
    {
        if (!CredentialContext.Current.IsRoot) { return -13; }
        if (mode > 1 || accent > 5 || materials > 7 || translucency > 100) { return -22; }
        lock (_gate)
        {
            var old = _saved;
            if (expectedRevision != old.Revision) { return -11; }
            if (old.Revision == ulong.MaxValue) { return -75; }
            try
            {
                _ = Parse(_store.Read(Path));
                uint level = translucency ?? old.Translucency;
                string text = "VERSION=" + (level != 40 ? "4" : materials > 3 ? "3" : "2") + "\nMODE=" + ModeName(mode) + "\nACCENT=" + AccentName(accent) + "\nMATERIALS=" + materials + "\n" + (level != 40 ? "TRANSLUCENCY=" + level + "\n" : "");
                _store.Replace(Path, text);
                _explicit = true; _saved = new(mode, accent, old.Revision + 1, materials, level);
                return 0;
            }
            catch { return -5; }
        }
    }
    public void MarkActive(AppearanceSelection selection) { _active = selection; }
    public void Resources(string? text)
    {
        lock (_gate) { _palette = AppearancePalette.Read(text); _resources++; }
    }
    public AppearanceState Snapshot()
    {
        lock (_gate) { return new(_saved, _active, _palette, _resources); }
    }
    public static string ModeName(uint mode) => mode == 0 ? "dark" : "light";
    public static string AccentName(uint accent) => new[] { "blue", "violet", "rose", "amber", "green", "teal" }[accent];
    public static bool TryMode(string name, out uint value)
    { value = name == "light" ? 1U : 0U; return name is "dark" or "light"; }
    public static bool TryAccent(string name, out uint value)
    {
        for (uint i = 0; i < 6; i++) { if (AccentName(i) == name) { value = i; return true; } }
        value = 0; return false;
    }
    private static (uint Mode, uint Accent, uint Materials, uint Translucency) Parse(string? text)
    {
        if (text is null) { return (0, 0, 0, 40); }
        if (Encoding.UTF8.GetByteCount(text) > 4096 || text.Contains('\0')) { throw new FormatException("invalid appearance configuration"); }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) { continue; }
            int eq = line.IndexOf('=');
            if (eq < 1 || !fields.TryAdd(line[..eq], line[(eq + 1)..])) { throw new FormatException("invalid/duplicate appearance key"); }
        }
        if (!fields.TryGetValue("VERSION", out var version) || version is not ("1" or "2" or "3" or "4")
            || fields.Count != (version == "1" ? 3 : version == "4" ? 5 : 4)
            || !fields.TryGetValue("MODE", out var mode) || !TryMode(mode, out uint m)
            || !fields.TryGetValue("ACCENT", out var accent) || !TryAccent(accent, out uint a))
        { throw new FormatException("unsupported appearance configuration"); }
        uint materials = 0;
        if (version != "1" && (!fields.TryGetValue("MATERIALS", out var effects) || effects.Length != 1 || effects[0] < '0' || effects[0] > (version is "3" or "4" ? '7' : '3')))
        { throw new FormatException("invalid material preferences"); }
        if (version != "1") { materials = (uint)(fields["MATERIALS"][0] - '0'); }
        uint level = 40;
        if (version == "4" && (!fields.TryGetValue("TRANSLUCENCY", out var value) || !uint.TryParse(value, out level) || level > 100))
        { throw new FormatException("invalid translucency preference"); }
        return (m, a, materials, level);
    }
}
