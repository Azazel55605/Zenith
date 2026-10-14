using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zenith.Core.Packages;

internal sealed record PackageFile(string Path, ushort Mode, int Length, string Digest);
internal sealed record PackageDependency(string Name, string Version);

/// <summary>Experimental P0 text manifest. This is not the production signed package format.</summary>
internal sealed class PackageManifest
{
    public const int MaximumBytes = 32768, MaximumFiles = 64, MaximumFileBytes = 262144, MaximumPayloadBytes = 4194304;
    public const string Runtime = "bootstrap-1";
    public string Name { get; }
    public string Version { get; }
    public string Architecture { get; }
    public string RuntimeId { get; }
    public string Kind { get; private set; } = "data";
    public string Repository { get; private set; } = "extra";
    public IReadOnlyList<string> Conflicts { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<PackageFile> Files { get; }
    public IReadOnlyList<PackageDependency> Dependencies { get; }
    public int PayloadBytes { get; }
    private PackageManifest(string name, string version, string architecture, string runtime, List<PackageFile> files, List<PackageDependency> dependencies, int bytes)
    { Name = name; Version = version; Architecture = architecture; RuntimeId = runtime; Files = files.AsReadOnly(); Dependencies = dependencies.AsReadOnly(); PayloadBytes = bytes; }

    public static PackageManifest Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes || text.IndexOf('\0') >= 0 || text.IndexOf('\r') >= 0)
        { throw new FormatException("invalid or oversized package manifest"); }
        foreach (char c in text)
        { if ((c < ' ' && c != '\t' && c != '\n') || c > '~') { throw new FormatException("manifest requires printable ASCII records"); } }
        string[] lines = text.Split('\n');
        if (lines.Length < 2 || !(lines[0] is "ZENITH-PACKAGE\t1" or "ZENITH-PACKAGE\t2" or "ZENITH-PACKAGE\t3")) { throw new FormatException("unsupported experimental manifest version"); }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<PackageFile>(); var dependencies = new List<PackageDependency>();
        bool version3 = lines[0].EndsWith("3", StringComparison.Ordinal);
        bool version2 = version3 || lines[0].EndsWith("2", StringComparison.Ordinal);
        var conflicts = new List<string>();
        var paths = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal);
        int bytes = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i]; if (i == lines.Length - 1 && line.Length == 0) { continue; }
            if (line.Length == 0 || line.Length > 1024) { throw new FormatException("invalid manifest line"); }
            string[] f = line.Split('\t');
            if (f[0] == "file")
            {
                if (f.Length != 5 || files.Count == MaximumFiles || !ValidPath(f[1]) || !paths.Add(f[1])
                    || (f[2] != "0644" && f[2] != "0755")
                    || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
                    || size.ToString(CultureInfo.InvariantCulture) != f[3] || size < 0 || size > MaximumFileBytes
                    || !ValidDigest(f[4])) { throw new FormatException("invalid package file record"); }
                bytes = checked(bytes + size);
                if (bytes > MaximumPayloadBytes) { throw new FormatException("package payload exceeds limit"); }
                files.Add(new(f[1], f[2] == "0644" ? (ushort)0x1a4 : (ushort)0x1ed, size, f[4]));
            }
            else if (f[0] == "requires")
            {
                if (f.Length != 3 || dependencies.Count == 32 || !ValidName(f[1]) || !(version2 ? PackageVersions.ValidConstraint(f[2]) : ValidVersion(f[2])) || !names.Add(f[1]))
                { throw new FormatException("invalid exact dependency"); }
                dependencies.Add(new(f[1], f[2]));
            }
            else if (version2 && f[0] == "conflicts")
            {
                if (f.Length != 2 || !ValidName(f[1]) || conflicts.Count == 32 || conflicts.Contains(f[1]))
                { throw new FormatException("invalid package conflict"); }
                conflicts.Add(f[1]);
            }
            else
            {
                if (f.Length != 2 || !(f[0] is "name" or "version" or "architecture" or "kind" or "runtime" || (version2 && f[0] == "repository")) || !fields.TryAdd(f[0], f[1]))
                { throw new FormatException("unknown or duplicate manifest field"); }
            }
        }
        if (fields.Count != (version2 ? 6 : 5) || !ValidName(fields["name"]) || !ValidVersion(fields["version"])
            || !(fields["architecture"] is "any" or "x86_64" or "arm64") || !(fields["kind"] == "data" || (version3 && fields["kind"] == "meta"))
            || fields["runtime"].Length is < 1 or > 64
            || (fields["kind"] == "meta" ? files.Count != 0 || dependencies.Count == 0 || fields["architecture"] != "any" : files.Count == 0)
            || (version2 && !(fields["repository"] is "core" or "extra")))
        { throw new FormatException("invalid package identity or unsupported kind"); }
        foreach (string path in paths)
        {
            for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
            { if (paths.Contains(path.Substring(0, slash))) { throw new FormatException("file is also a parent path"); } }
        }
        var result = new PackageManifest(fields["name"], fields["version"], fields["architecture"], fields["runtime"], files, dependencies, bytes);
        result.Repository = version2 ? fields["repository"] : "extra"; result.Conflicts = conflicts.AsReadOnly();
        result.Kind = fields["kind"];
        if (conflicts.Contains(result.Name)) { throw new FormatException("package conflicts with itself"); }
        return result;
    }
    public static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 64 || name[0] is < 'a' or > 'z') { return false; }
        foreach (char c in name) { if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) { return false; } }
        return true;
    }
    private static bool ValidVersion(string version)
    {
        string[] parts = version.Split('.'); if (parts.Length is < 2 or > 3) { return false; }
        foreach (string part in parts)
        { if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) || n.ToString(CultureInfo.InvariantCulture) != part) { return false; } }
        return true;
    }
    public static bool ValidPath(string path)
    {
        if (path.Length > 240 || !(path.StartsWith("/usr/share/doc/", StringComparison.Ordinal)
            || path.StartsWith("/usr/share/zenith/", StringComparison.Ordinal)) || path.EndsWith('/')) { return false; }
        foreach (char c in path)
        { if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '/' or '-' or '_' or '.')) { return false; } }
        foreach (string part in path.Substring(1).Split('/')) { if (part is "" or "." or "..") { return false; } }
        // Prototype packages never replace bootstrap inventory or account/settings state.
        return path != "/usr/share/zenith/components.tsv" && !path.StartsWith("/usr/share/zenith/components.tsv/", StringComparison.Ordinal);
    }
    private static bool ValidDigest(string text)
    {
        if (text.Length != 64) { return false; }
        foreach (char c in text) { if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return false; } }
        return true;
    }
}
