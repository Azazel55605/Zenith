using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Zenith.Core.Security;

namespace Zenith.Core.Configuration;

internal readonly record struct DesktopEntry(uint Id, string Name);

/// <summary>Immutable by convention: arrays belong to the snapshot and are replaced on save.</summary>
internal sealed record DesktopPreferences
{
    public const int MaximumDesktops = 8, MaximumPins = 6;
    public const int MinimumTileSplit = 25, DefaultTileSplit = 55, MaximumTileSplit = 75;
    public const int MaximumTileGap = 32;
    public int MasterPercent { get; init; } = DefaultTileSplit;
    public int TileInnerGap { get; init; }
    public int TileOuterGap { get; init; }
    public uint TaskbarPosition { get; init; }
    public string[] TaskbarPins { get; init; } = Array.Empty<string>();
    public uint FocusMode { get; init; }
    public uint FocusDelay { get; init; } = 250;
    public uint RaiseOnClick { get; init; } = 1;
    public uint RaiseOnFocus { get; init; }
    public uint TitleDoubleClick { get; init; }
    public uint MaximizeButtonAction { get; init; }
    public uint ScreenSnap { get; init; } = 1;
    public uint WindowSnapDistance { get; init; }
    public uint SwitcherLayout { get; init; }
    public uint SwitcherSort { get; init; }
    public uint SwitcherGroup { get; init; }
    public uint SwitcherScope { get; init; }
    public DesktopEntry[] Desktops { get; init; } = new DesktopEntry[] { new(0, "Desktop 1") };
    public uint DesktopMotion { get; init; } = 1;

    public static int ChoiceCount(uint page) => page switch { 1 => 2, 2 => 8, 3 => 4, 4 => 1, 5 => 10, _ => 0 };

    public uint[] Choices(uint page, uint taskbar) => page switch
    {
        1 => new[] { taskbar, TaskbarPosition },
        2 => new[] { FocusMode, FocusDelay, RaiseOnClick, RaiseOnFocus, TitleDoubleClick, MaximizeButtonAction, ScreenSnap, WindowSnapDistance },
        3 => new[] { SwitcherLayout, SwitcherSort, SwitcherGroup, SwitcherScope },
        4 => new[] { DesktopMotion },
        5 => [.. Choices(2, taskbar), (uint)TileInnerGap, (uint)TileOuterGap],
        _ => Array.Empty<uint>()
    };

    public DesktopEntry[] Entries(uint page)
    {
        if (page == 4) { return Desktops; }
        if (page != 1) { return Array.Empty<DesktopEntry>(); }
        var result = new DesktopEntry[TaskbarPins.Length];
        for (int i = 0; i < result.Length; i++) { result[i] = new(0, TaskbarPins[i]); }
        return result;
    }

    public DesktopPreferences? Edit(uint page, uint[] values, DesktopEntry[] entries)
    {
        if (page == 1 && values[0] < SettingsService.TaskbarNames.Length && values[1] < 2 && entries.Length <= MaximumPins)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pins = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Id != 0 || !ValidName(entry.Name, true) || !seen.Add(entry.Name)) { return null; }
                pins[i] = entry.Name;
            }
            return this with { TaskbarPosition = values[1], TaskbarPins = pins };
        }
        if (page is 2 or 5 && entries.Length == 0 && values[0] < 2 && values[1] <= 1000 && values[2] < 2 && values[3] < 2
            && values[4] < 4 && values[5] < 4 && values[6] < 2 && values[7] <= 32
            && (page == 2 || (values[8] <= MaximumTileGap && values[9] <= MaximumTileGap)))
        {
            return this with { FocusMode = values[0], FocusDelay = values[1], RaiseOnClick = values[2], RaiseOnFocus = values[3],
                TitleDoubleClick = values[4], MaximizeButtonAction = values[5], ScreenSnap = values[6], WindowSnapDistance = values[7],
                TileInnerGap = page == 5 ? (int)values[8] : TileInnerGap, TileOuterGap = page == 5 ? (int)values[9] : TileOuterGap };
        }
        if (page == 3 && entries.Length == 0 && values[0] < 2 && values[1] < 3 && values[2] < 2 && values[3] < 2)
        {
            return this with { SwitcherLayout = values[0], SwitcherSort = values[1], SwitcherGroup = values[2], SwitcherScope = values[3] };
        }
        if (page == 4 && values[0] < 2 && entries.Length is >= 1 and <= MaximumDesktops)
        {
            var seen = new HashSet<uint>();
            foreach (var entry in entries)
            {
                if (entry.Id > 1_000_000_000 || !seen.Add(entry.Id) || !ValidName(entry.Name, false)) { return null; }
            }
            return this with { DesktopMotion = values[0], Desktops = (DesktopEntry[])entries.Clone() };
        }
        return null;
    }

    private static bool ValidName(string name, bool app)
    {
        if (name.Length == 0 || name != name.Trim() || Encoding.UTF8.GetByteCount(name) > (app ? 64 : 48)) { return false; }
        foreach (char c in name)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format || (app && c > '~')) { return false; }
        }
        return true;
    }
}

internal sealed partial class SettingsService
{
    public const string DesktopPath = "/etc/zenith/desktop-preferences.conf";
    public DesktopPreferences DesktopPreferences { get; private set; } = new();
    public (DesktopPreferences Preferences, uint Taskbar, ulong Revision) DesktopSnapshot()
    { lock (_gate) { return (DesktopPreferences, Current.Taskbar, _revision); } }

    private static uint[] ReadDesktopChoices(Dictionary<string, string> fields, string key, uint[] defaults)
    {
        if (!fields.TryGetValue(key, out string? text)) { return defaults; }
        string[] parts = text.Split(',');
        if (parts.Length != defaults.Length) { throw new FormatException("invalid " + key); }
        var values = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++) { values[i] = uint.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture); }
        return values;
    }

    private static DesktopEntry[] ReadDesktopEntries(Dictionary<string, string> fields, string key, DesktopEntry[] defaults)
    {
        if (!fields.TryGetValue(key, out string? text)) { return defaults; }
        if (text.Length == 0) { return Array.Empty<DesktopEntry>(); }
        var entries = new List<DesktopEntry>();
        foreach (string item in text.Split(','))
        {
            string[] pair = item.Split(':');
            if (pair.Length != 2 || entries.Count == Zenith.Core.Configuration.DesktopPreferences.MaximumDesktops)
            { throw new FormatException("invalid " + key); }
            entries.Add(new(uint.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false, true).GetString(Convert.FromBase64String(pair[1]))));
        }
        return entries.ToArray();
    }

    private static string EncodeDesktopEntries(DesktopEntry[] entries)
    {
        var result = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++) { result[i] = entries[i].Id + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(entries[i].Name)); }
        return string.Join(",", result);
    }

    private void LoadDesktopPreferences(List<string> errors, uint taskbar)
    {
        DesktopPreferences = new();
        try
        {
            var fields = Fields(_store.Read(TaskbarPath));
            uint[] choices = ReadDesktopChoices(fields, "POSITION", new uint[] { 0 });
            DesktopPreferences = DesktopPreferences.Edit(1, new[] { taskbar, choices[0] },
                ReadDesktopEntries(fields, "PINS", Array.Empty<DesktopEntry>())) ?? throw new FormatException("invalid panel preferences");
        }
        catch (Exception e) { errors.Add(TaskbarPath + ": " + e.Message + "; using default placement and pins"); }

        try
        {
            var fields = Fields(_store.Read(DesktopPath));
            CheckDesktopVersion(fields);
            if (fields.TryGetValue("MASTER_PERCENT", out string? value))
            {
                if (TryTileSplit(value, out int percent)) { DesktopPreferences = DesktopPreferences with { MasterPercent = percent }; }
                else { errors.Add(DesktopPath + ": invalid MASTER_PERCENT; using default split"); }
            }
            if (fields.TryGetValue("WM_GAPS", out string? gaps))
            {
                string[] pair = gaps.Split(',');
                if (pair.Length == 2 && TryTileGap(pair[0], out int inner) && TryTileGap(pair[1], out int outer))
                { DesktopPreferences = DesktopPreferences with { TileInnerGap = inner, TileOuterGap = outer }; }
                else { errors.Add(DesktopPath + ": invalid WM_GAPS; using default gaps"); }
            }
            for (uint page = 2; page <= 4; page++)
            {
                string key = DesktopKey(page);
                try
                {
                    DesktopPreferences = DesktopPreferences.Edit(page,
                        ReadDesktopChoices(fields, key, DesktopPreferences.Choices(page, taskbar)),
                        ReadDesktopEntries(fields, key + "_ENTRIES", DesktopPreferences.Entries(page)))
                        ?? throw new FormatException("invalid " + key);
                }
                catch (Exception e) { errors.Add(DesktopPath + ": " + e.Message + "; using defaults for " + key); }
            }
        }
        catch (Exception e) { errors.Add(DesktopPath + ": " + e.Message + "; using defaults"); }
    }

    private static bool TryTileSplit(string value, out int percent) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out percent)
        && percent is >= DesktopPreferences.MinimumTileSplit and <= DesktopPreferences.MaximumTileSplit;

    public void SetTileSplit(string value)
    {
        RequireRoot();
        if (!TryTileSplit(value, out int percent)) { throw new ArgumentException("tile-split takes a percentage from 25 to 75"); }
        WriteSettings(() =>
        {
            var fields = new Dictionary<string, string>
            {
                ["VERSION"] = "1",
                ["MASTER_PERCENT"] = percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            ReplaceDesktopFields(DesktopPath, fields);
            DesktopPreferences = DesktopPreferences with { MasterPercent = percent };
        });
    }

    private static bool TryTileGap(string value, out int gap) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out gap) && gap is >= 0 and <= DesktopPreferences.MaximumTileGap;

    private static string EncodeTileGaps(int inner, int outer) =>
        inner.ToString(CultureInfo.InvariantCulture) + "," + outer.ToString(CultureInfo.InvariantCulture);

    public void SetTileGaps(string inner, string outer)
    {
        RequireRoot();
        if (!TryTileGap(inner, out int innerGap) || !TryTileGap(outer, out int outerGap))
        { throw new ArgumentException("tile-gaps takes inner and outer gaps from 0 to 32 logical pixels"); }
        WriteSettings(() =>
        {
            ReplaceDesktopFields(DesktopPath, new() { ["VERSION"] = "1", ["WM_GAPS"] = EncodeTileGaps(innerGap, outerGap) });
            DesktopPreferences = DesktopPreferences with { TileInnerGap = innerGap, TileOuterGap = outerGap };
        });
    }

    private void ReplaceDesktopFields(string path, Dictionary<string, string> fields)
    {
        string? old = _store.Read(path);
        CheckDesktopVersion(Fields(old));
        _store.Replace(path, Update(old, fields));
    }

    private static void CheckDesktopVersion(Dictionary<string, string> fields)
    {
        if (fields.TryGetValue("VERSION", out string? version) && version != "1") { throw new FormatException("unsupported settings VERSION"); }
    }

    private static string DesktopKey(uint page) => page switch { 2 => "WINDOWS", 3 => "SWITCHER", 4 => "WORKSPACES", _ => throw new ArgumentOutOfRangeException(nameof(page)) };

    public int SaveDesktopPreferences(uint page, uint[] choices, DesktopEntry[] entries, ulong revision)
    {
        if (!CredentialContext.Current.IsRoot) { return -13; }
        int fieldsCount = DesktopPreferences.ChoiceCount(page);
        if (fieldsCount == 0 || choices.Length != fieldsCount) { return -22; }
        lock (_gate)
        {
            if (revision != _revision) { return -11; }
            if (_revision == ulong.MaxValue) { return -75; }
            if (DesktopPreferences.Edit(page, choices, entries) is not DesktopPreferences next) { return -22; }
            try
            {
                string path = page == 1 ? TaskbarPath : DesktopPath;
                var fields = new Dictionary<string, string> { ["VERSION"] = "1" };
                if (page == 1)
                {
                    fields["TASKBAR"] = TaskbarNames[choices[0]];
                    fields["POSITION"] = choices[1].ToString(System.Globalization.CultureInfo.InvariantCulture);
                    fields["PINS"] = EncodeDesktopEntries(entries);
                }
                else if (page == 5)
                {
                    fields["WINDOWS"] = string.Join(",", next.Choices(2, 0));
                    fields["WM_GAPS"] = EncodeTileGaps(next.TileInnerGap, next.TileOuterGap);
                }
                else
                {
                    string key = DesktopKey(page);
                    fields[key] = string.Join(",", choices);
                    if (page == 4) { fields[key + "_ENTRIES"] = EncodeDesktopEntries(entries); }
                }
                ReplaceDesktopFields(path, fields);
            }
            catch { return -5; }
            DesktopPreferences = next;
            if (page == 1) { Current = Current with { Taskbar = choices[0] }; }
            _revision++;
            return 0;
        }
    }
}
