using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zenith.Core.Packages;

internal interface IPackageStore
{
    PackagePathState State(string path);
    byte[] Read(string path, int maximum);
    ushort Mode(string path);
    void Directory(string path);
    void Write(string path, byte[] bytes, ushort mode);
    void Delete(string path);
    void Sync();
    ulong FreeBytes { get; }
}
internal sealed record InstalledPackage(string ManifestText, string ArchiveDigest, string KeyId)
{
    public PackageManifest Manifest => PackageManifest.Parse(ManifestText);
}

/// <summary>Root-only callers; bounded offline transactions with durable undo and DB commit detection.</summary>
internal sealed class PackageManager
{
    public const string Root = "/var/lib/zenith/pkg", Database = Root + "/installed.tsv", Journal = Root + "/journal.tsv";
    private const int MaximumDatabase = 1048576, MaximumTransaction = 8388608;
    private readonly IPackageStore _store;
    public PackageManager(IPackageStore store) { _store = store; }
    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    private static string String(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
    public IReadOnlyList<InstalledPackage> List()
    {
        if (_store.State(Journal) != PackagePathState.Missing) { throw new IOException("unfinished transaction; root must run pkg recover"); }
        return ParseDatabase(_store.State(Database) == PackagePathState.Missing ? Array.Empty<byte>() : _store.Read(Database, MaximumDatabase));
    }
    private static List<InstalledPackage> ParseDatabase(byte[] bytes)
    {
        var result = new List<InstalledPackage>(); if (bytes.Length == 0) { return result; }
        string[] lines = String(bytes).Split('\n');
        if (lines[0] != "ZENITH-INSTALLED\t1") { throw new IOException("unsupported installed database"); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines.AsSpan(1))
        {
            if (line.Length == 0) { continue; }
            string[] fields = line.Split('\t');
            if (fields.Length != 3 || result.Count == 32 || !Hex(fields[0]) || !Hex(fields[1])) { throw new IOException("invalid installed record"); }
            string manifestText = String(Convert.FromBase64String(fields[2]));
            var manifest = PackageManifest.Parse(manifestText);
            if (!names.Add(manifest.Name)) { throw new IOException("duplicate installed package"); }
            result.Add(new(manifestText, fields[0], fields[1]));
        }
        ValidateSet(result, false); return result;
    }
    private static bool Hex(string text)
    {
        if (text.Length != 64) { return false; }
        foreach (char c in text) { if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return false; } }
        return true;
    }
    private static byte[] Serialize(IReadOnlyList<InstalledPackage> packages)
    {
        var text = new StringBuilder("ZENITH-INSTALLED\t1\n");
        foreach (var item in packages)
        { text.Append(item.ArchiveDigest).Append('\t').Append(item.KeyId).Append('\t').Append(Convert.ToBase64String(Text(item.ManifestText))).Append('\n'); }
        byte[] bytes = Text(text.ToString()); if (bytes.Length > MaximumDatabase) { throw new IOException("installed DB exceeds limit"); } return bytes;
    }
    public static void ValidateSet(IReadOnlyList<InstalledPackage> packages, bool dependencies = true)
    {
        if (packages.Count > 32) { throw new IOException("installed package limit"); }
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in BootstrapComponents.All) { versions.Add(component.Name, component.Version); }
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in packages)
        {
            var manifest = item.Manifest;
            if (!versions.TryAdd(manifest.Name, manifest.Version)) { throw new IOException("duplicate or bundled identity: " + manifest.Name); }
            if (manifest.Architecture is not ("any" or "x86_64") || manifest.RuntimeId != PackageManifest.Runtime)
            { throw new IOException("architecture/runtime mismatch: " + manifest.Name); }
            foreach (var file in manifest.Files)
            { if (!paths.Add(file.Path)) { throw new IOException("file ownership conflict: " + file.Path); } }
        }
        foreach (string path in paths)
        {
            for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
            { if (paths.Contains(path.Substring(0, slash))) { throw new IOException("owned file/ancestor conflict: " + path); } }
        }
        if (!dependencies) { return; }
        foreach (var item in packages)
        {
            var manifest = item.Manifest;
            foreach (var need in manifest.Dependencies)
            {
                if (!versions.TryGetValue(need.Name, out var version) || !PackageVersions.Matches(version, need.Version))
                { throw new IOException("unsatisfied dependency: " + manifest.Name + " requires " + need.Name + " " + need.Version); }
            }
            foreach (string conflict in manifest.Conflicts)
            { if (versions.ContainsKey(conflict)) { throw new IOException("declared conflict: " + manifest.Name + " conflicts with " + conflict); } }
        }
        // Cycles in data packages have no executable hook ordering: install the complete
        // satisfied set atomically. Missing dependencies never trigger partial installs.
    }
    public IReadOnlyList<string> Verify()
    {
        var errors = new List<string>();
        foreach (var item in List())
        {
            foreach (var file in item.Manifest.Files)
            { try { Owned(file); } catch (Exception) { errors.Add("modified/missing: " + file.Path); } }
        }
        return errors.AsReadOnly();
    }
    private void Parents(string path)
    {
        for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            string parent = path.Substring(0, slash); var state = _store.State(parent);
            if (state == PackagePathState.Occupied) { throw new IOException("non-directory or linked ancestor: " + parent); }
        }
    }
    private void EnsureParents(string path)
    {
        Parents(path);
        for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
        { string parent = path.Substring(0, slash); if (_store.State(parent) == PackagePathState.Missing) { _store.Directory(parent); } }
    }
    private byte[] Owned(PackageFile file)
    {
        Parents(file.Path); byte[] bytes = _store.Read(file.Path, PackageManifest.MaximumFileBytes);
        if (bytes.Length != file.Length || PackagePlanner.Digest(bytes) != file.Digest || _store.Mode(file.Path) != file.Mode)
        { throw new IOException("locally modified owned file; preserve it before changing package: " + file.Path); }
        return bytes;
    }
    public void Install(IReadOnlyList<SignedPackage> incoming, Action cancel)
    {
        if (incoming.Count is < 1 or > 16) { throw new IOException("install expects 1..16 signed packages"); }
        Recover(); var old = new List<InstalledPackage>(List()); var next = new List<InstalledPackage>(old);
        var seen = new HashSet<string>(StringComparer.Ordinal); var changes = new Dictionary<string, (byte[]? Data, ushort Mode)>(StringComparer.Ordinal);
        foreach (var package in incoming)
        {
            cancel(); var manifest = package.Manifest;
            if (!seen.Add(manifest.Name)) { throw new IOException("multiple candidate versions: " + manifest.Name); }
            var existing = next.Find(x => x.Manifest.Name == manifest.Name);
            if (existing is not null)
            {
                if (PackageVersions.Compare(manifest.Version, existing.Manifest.Version) < 0) { throw new IOException("downgrade refused"); }
                next.Remove(existing);
                foreach (var file in existing.Manifest.Files) { _ = Owned(file); changes[file.Path] = (null, file.Mode); }
            }
            next.Add(new(package.ManifestText, package.ArchiveDigest, package.KeyId));
            foreach (var file in manifest.Files)
            {
                Parents(file.Path);
                if (file.Path.EndsWith(".pkg-new", StringComparison.Ordinal)) { throw new IOException("reserved staging suffix"); }
                bool owned = existing is not null && Contains(existing.Manifest.Files, file.Path);
                if (!owned && _store.State(file.Path) != PackagePathState.Missing) { throw new IOException("unowned path conflict: " + file.Path); }
                changes[file.Path] = (package.Read(file), file.Mode);
            }
        }
        ValidateSet(next); Commit(old, next, changes, cancel);
    }
    private static bool Contains(IReadOnlyList<PackageFile> files, string path)
    { foreach (var file in files) { if (file.Path == path) { return true; } } return false; }
    public void Remove(string name, Action cancel) => Remove(new[] { name }, cancel);
    public void Remove(IReadOnlyList<string> names, Action cancel)
    {
        if (names.Count is < 1 or > 16) { throw new IOException("remove expects 1..16 package names"); }
        Recover(); var old = new List<InstalledPackage>(List()); var next = new List<InstalledPackage>(old);
        var changes = new Dictionary<string, (byte[]? Data, ushort Mode)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!seen.Add(name)) { throw new IOException("duplicate removal target"); }
            var item = next.Find(x => x.Manifest.Name == name) ?? throw new IOException("package is not independently installed: " + name);
            next.Remove(item);
            foreach (var file in item.Manifest.Files) { cancel(); _ = Owned(file); changes.Add(file.Path, (null, file.Mode)); }
        }
        ValidateSet(next); Commit(old, next, changes, cancel);
    }
    private void Commit(List<InstalledPackage> old, List<InstalledPackage> next,
        Dictionary<string, (byte[]? Data, ushort Mode)> changes, Action cancel)
    {
        if (changes.Count > 1024) { throw new IOException("transaction file limit"); }
        byte[] before = Serialize(old), after = Serialize(next); int bytes = 0;
        var backups = new List<(string Path, byte[]? Data, ushort Mode)>();
        foreach (var change in changes)
        {
            Parents(change.Key);
            if (_store.State(change.Key + ".pkg-new") != PackagePathState.Missing) { throw new IOException("occupied package staging path"); }
            byte[]? data = _store.State(change.Key) == PackagePathState.Missing ? null : _store.Read(change.Key, PackageManifest.MaximumFileBytes);
            bytes = checked(bytes + (data?.Length ?? 0) + (change.Value.Data?.Length ?? 0));
            backups.Add((change.Key, data, data is null ? (ushort)0 : _store.Mode(change.Key)));
        }
        if (bytes > MaximumTransaction || _store.FreeBytes < (ulong)(bytes + before.Length + after.Length + 262144))
        { throw new IOException("insufficient transaction space or transaction exceeds 8 MiB"); }
        EnsureParents(Journal); var journal = new StringBuilder("ZENITH-UNDO\t1\t" + PackagePlanner.Digest(after) + "\n");
        try
        {
            _store.Write(Root + "/old-db", before, 0x180);
            for (int i = 0; i < backups.Count; i++)
            {
                cancel(); var backup = backups[i];
                if (backup.Data is not null) { _store.Write(Root + "/undo-" + i, backup.Data, 0x180); }
                journal.Append(backup.Path).Append('\t').Append(backup.Data is null ? "missing" : backup.Mode.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(backup.Data is null ? "-" : PackagePlanner.Digest(backup.Data)).Append('\n');
            }
            _store.Sync(); _store.Write(Journal, Text(journal.ToString()), 0x180); _store.Sync();
            foreach (var change in changes)
            {
                cancel(); Parents(change.Key);
                // Commands are serialized; revalidate the owned snapshot before mutation.
                var saved = backups.Find(x => x.Path == change.Key);
                if (saved.Data is null ? _store.State(change.Key) != PackagePathState.Missing
                    : PackagePlanner.Digest(_store.Read(change.Key, PackageManifest.MaximumFileBytes)) != PackagePlanner.Digest(saved.Data))
                { throw new IOException("target changed during transaction: " + change.Key); }
                if (change.Value.Data is null) { _store.Delete(change.Key); }
                else { EnsureParents(change.Key); _store.Write(change.Key, change.Value.Data, change.Value.Mode); }
            }
            _store.Sync(); _store.Write(Database, after, 0x1a4); _store.Sync();
            Cleanup(backups.Count);
        }
        catch
        {
            // Cosmos skips unwind finally: cleanup is explicit in the owning catch.
            if (_store.State(Journal) != PackagePathState.Missing) { Recover(); }
            else { Cleanup(backups.Count); }
            throw;
        }
    }
    private void Cleanup(int count)
    {
        // Retire the durable marker first. Leftover private backups are inert and
        // will be replaced by the next transaction; never delete backups under a live marker.
        _store.Delete(Journal); _store.Sync();
        _store.Delete(Root + "/old-db");
        for (int i = 0; i < count; i++) { _store.Delete(Root + "/undo-" + i); }
    }
    public void Recover()
    {
        Parents(Journal); if (_store.State(Journal) == PackagePathState.Missing) { return; }
        string[] lines = String(_store.Read(Journal, 262144)).Split('\n'); string[] header = lines[0].Split('\t');
        if (header.Length != 3 || header[0] != "ZENITH-UNDO" || header[1] != "1" || !Hex(header[2])) { throw new IOException("invalid transaction journal"); }
        var entries = new List<(string Path, byte[]? Data, ushort Mode)>(); var seen = new HashSet<string>(StringComparer.Ordinal); int recoveredBytes = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) { continue; }
            string[] fields = lines[i].Split('\t');
            if (fields.Length != 3 || !PackageManifest.ValidPath(fields[0]) || !seen.Add(fields[0]) || entries.Count == 1024)
            { throw new IOException("invalid undo path/count"); }
            Parents(fields[0]); byte[]? data = null; ushort mode = 0;
            if (fields[1] != "missing")
            {
                if (!ushort.TryParse(fields[1], out mode) || mode is not (0x1a4 or 0x1ed)) { throw new IOException("invalid undo mode"); }
                data = _store.Read(Root + "/undo-" + entries.Count, PackageManifest.MaximumFileBytes);
                recoveredBytes = checked(recoveredBytes + data.Length);
                if (recoveredBytes > MaximumTransaction) { throw new IOException("undo payload limit exceeded"); }
                if (PackagePlanner.Digest(data) != fields[2]) { throw new IOException("invalid undo digest"); }
            }
            else if (fields[2] != "-") { throw new IOException("invalid missing undo record"); }
            entries.Add((fields[0], data, mode));
        }
        bool committed = _store.State(Database) != PackagePathState.Missing && PackagePlanner.Digest(_store.Read(Database, MaximumDatabase)) == header[2];
        if (!committed)
        {
            byte[] before = _store.Read(Root + "/old-db", MaximumDatabase); _ = ParseDatabase(before);
            foreach (var entry in entries)
            { if (entry.Data is null) { _store.Delete(entry.Path); } else { EnsureParents(entry.Path); _store.Write(entry.Path, entry.Data, entry.Mode); } }
            _store.Write(Database, before, 0x1a4); _store.Sync();
        }
        Cleanup(entries.Count);
    }
}
