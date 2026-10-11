using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Core.Packages;

internal sealed record PackageRepository(string Name, int Priority, string? Mirror);
internal static class PackageRepositories
{
    public const string Path = "/etc/zenith/repos.conf";
    public const string Default = "ZENITH-REPOS\t1\ncore\t10\t-\nextra\t20\t-\n";
    public static IReadOnlyList<PackageRepository> Parse(string text)
    {
        if (text.Length > 4096) { throw new FormatException("repository config exceeds limit"); }
        string[] lines = text.Split('\n'); if (lines[0] != "ZENITH-REPOS\t1") { throw new FormatException("unsupported repository configuration"); }
        var result = new List<PackageRepository>(); var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i] == "") { continue; }
            string[] f = lines[i].Split('\t');
            if (f.Length != 3 || !(f[0] is "core" or "extra") || !names.Add(f[0])
                || !int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int priority) || priority is < 1 or > 100)
            { throw new FormatException("invalid or duplicate repository"); }
            if (f[2] != "-")
            {
                if (f[2].Length > 256 || !Uri.TryCreate(f[2], UriKind.Absolute, out var uri) || uri.Scheme != "https"
                    || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                { throw new FormatException("repository mirror must be a plain HTTPS base URL"); }
            }
            result.Add(new(f[0], priority, f[2] == "-" ? null : f[2]));
        }
        if (result.Count != 2) { throw new FormatException("core and extra repository definitions required"); }
        result.Sort((a, b) => a.Priority == b.Priority ? string.CompareOrdinal(a.Name, b.Name) : a.Priority.CompareTo(b.Priority));
        return result.AsReadOnly();
    }
}
