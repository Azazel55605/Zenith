using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zenith.Core.Packages;

/// <summary>One package transaction as Orbit's History shows it (kernel 0.0.70, docs/components/ORBIT.md).</summary>
/// <param name="Changes">What changed, in plain words, e.g. "install zenith-polaris 0.1.0",
/// "upgrade zenith-settings 0.12.0 0.13.0", "remove sunbeam-tools 0.1.0", "kernel 0.0.69 0.0.70".</param>
internal sealed record PackageTransaction(long Time, uint Uid, string User, string Source, bool Succeeded,
    IReadOnlyList<string> Changes, string Command, string Message);

/// <summary>The transaction history every package change appends to, whoever started it (Orbit, the shell or the
/// installer): <c>/var/lib/zenith/pkg/history.tsv</c>, the newest 64 transactions, bounded and sanitized.</summary>
internal static class PackageHistory
{
    public const string Path = PackageManager.Root + "/history.tsv";
    public const int Kept = 64, MaximumChanges = 32, MaximumBytes = 98304;
    private const string Header = "ZENITH-PKG-HISTORY\t1";

    /// <summary>Who is changing packages right now. The package service runs one change at a time, so whoever holds
    /// it sets these first (the shell sets "shell" and its command line; Orbit's job sets "Orbit").</summary>
    public static string Source = "shell", Command = "";
    /// <summary>The clock and the caller, bound by the running system (host tests keep the defaults).</summary>
    public static Func<long> Now = () => 0;
    public static Func<(uint Uid, string User)> Who = () => (0, "root");

    public static string Clean(string text, int maximum)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maximum));
        foreach (char c in text)
        {
            if (builder.Length == maximum) { break; }
            builder.Append(c is < ' ' or > '~' ? ' ' : c);
        }
        return builder.ToString().Trim();
    }

    public static List<PackageTransaction> Parse(string text)
    {
        var result = new List<PackageTransaction>();
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || lines[0] != Header) { return result; }
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split('\t');
            if (f.Length != 8 || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time)
                || !uint.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) || f[4] is not ("ok" or "failed")) { continue; }
            var changes = new List<string>();
            foreach (string change in f[5].Split(';')) { if (change.Length > 0 && changes.Count < MaximumChanges) { changes.Add(Clean(change, 120)); } }
            result.Add(new(time, uid, Clean(f[2], 32), Clean(f[3], 16), f[4] == "ok", changes.AsReadOnly(), Clean(f[6], 240), Clean(f[7], 240)));
        }
        if (result.Count > Kept) { result.RemoveRange(0, result.Count - Kept); }
        return result;
    }

    public static string Serialize(IReadOnlyList<PackageTransaction> transactions)
    {
        var text = new StringBuilder(Header).Append('\n');
        int first = Math.Max(0, transactions.Count - Kept);
        for (int i = first; i < transactions.Count; i++)
        {
            var t = transactions[i];
            var changes = new StringBuilder();
            for (int c = 0; c < t.Changes.Count && c < MaximumChanges; c++)
            { if (c > 0) { changes.Append(';'); } changes.Append(Clean(t.Changes[c], 120).Replace(";", ",")); }
            text.Append(t.Time.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(t.Uid.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(Clean(t.User, 32)).Append('\t').Append(Clean(t.Source, 16)).Append('\t').Append(t.Succeeded ? "ok" : "failed").Append('\t')
                .Append(changes).Append('\t').Append(Clean(t.Command, 240)).Append('\t').Append(Clean(t.Message, 240)).Append('\n');
        }
        return text.ToString();
    }

    public static List<PackageTransaction> Read(IPackageStore store)
    {
        try
        {
            if (store.State(Path) == PackagePathState.Missing) { return new(); }
            return Parse(Encoding.UTF8.GetString(store.Read(Path, MaximumBytes)));
        }
        catch (Exception) { return new(); }
    }

    /// <summary>Appends a transaction; never throws (the history must not undo a committed change).</summary>
    public static bool Append(IPackageStore store, PackageTransaction transaction)
    {
        try
        {
            var all = Read(store); all.Add(transaction);
            byte[] bytes = Encoding.UTF8.GetBytes(Serialize(all));
            while (bytes.Length > MaximumBytes && all.Count > 1) { all.RemoveAt(0); bytes = Encoding.UTF8.GetBytes(Serialize(all)); }
            store.Write(Path, bytes, 0x1a4); store.Sync();
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The changes between two installed sets, in name order: install, upgrade, remove.</summary>
    public static List<string> Changes(IReadOnlyList<InstalledPackage> before, IReadOnlyList<InstalledPackage> after)
    {
        var old = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in before) { var m = item.Manifest; old[m.Name] = m.Version; }
        var next = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in after) { var m = item.Manifest; next[m.Name] = m.Version; }
        var result = new List<string>();
        foreach (var pair in next)
        {
            if (!old.TryGetValue(pair.Key, out var version)) { result.Add("install " + pair.Key + " " + pair.Value); }
            else if (version != pair.Value) { result.Add("upgrade " + pair.Key + " " + version + " " + pair.Value); }
            else if (!SameDigest(before, after, pair.Key)) { result.Add("reinstall " + pair.Key + " " + pair.Value); }
        }
        var removed = new List<string>(old.Keys); removed.Sort(StringComparer.Ordinal);
        foreach (string name in removed) { if (!next.ContainsKey(name)) { result.Add("remove " + name + " " + old[name]); } }
        return result;
    }
    private static bool SameDigest(IReadOnlyList<InstalledPackage> before, IReadOnlyList<InstalledPackage> after, string name)
    {
        string? a = null, b = null;
        foreach (var item in before) { if (item.Manifest.Name == name) { a = item.ArchiveDigest; } }
        foreach (var item in after) { if (item.Manifest.Name == name) { b = item.ArchiveDigest; } }
        return a == b;
    }

    /// <summary>A change in plain words: "Installed Polaris 0.1.0", "Upgraded zenith-settings 0.12.0 → 0.13.0".</summary>
    public static string Words(string change)
    {
        string[] f = change.Split(' ');
        return f[0] switch
        {
            "install" when f.Length == 3 => "Installed " + f[1] + " " + f[2],
            "upgrade" when f.Length == 4 => "Upgraded " + f[1] + " " + f[2] + " to " + f[3],
            "reinstall" when f.Length == 3 => "Reinstalled " + f[1] + " " + f[2],
            "remove" when f.Length == 3 => "Removed " + f[1] + " " + f[2],
            "kernel" when f.Length == 3 => "Staged kernel " + f[2] + " (was " + f[1] + "), active after a restart",
            _ => change,
        };
    }
}
