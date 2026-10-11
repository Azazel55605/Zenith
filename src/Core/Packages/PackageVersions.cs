using System;
using System.Globalization;

namespace Zenith.Core.Packages;

internal static class PackageVersions
{
    public static bool Valid(string version)
    {
        string[] parts = version.Split('.'); if (parts.Length is < 2 or > 3) { return false; }
        foreach (string p in parts)
        { if (!uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) || n.ToString(CultureInfo.InvariantCulture) != p) { return false; } }
        return true;
    }
    public static int Compare(string left, string right)
    {
        if (!Valid(left) || !Valid(right)) { throw new FormatException("invalid numeric package version"); }
        string[] a = left.Split('.'), b = right.Split('.');
        for (int i = 0; i < 3; i++)
        {
            uint x = i < a.Length ? uint.Parse(a[i], CultureInfo.InvariantCulture) : 0;
            uint y = i < b.Length ? uint.Parse(b[i], CultureInfo.InvariantCulture) : 0;
            if (x != y) { return x < y ? -1 : 1; }
        }
        return 0;
    }
    public static bool ValidConstraint(string constraint)
    {
        if (constraint.Length > 96) { return false; }
        string[] terms = constraint.Split(','); if (terms.Length is < 1 or > 2) { return false; }
        foreach (string term in terms) { if (!Valid(Version(term))) { return false; } }
        return true;
    }
    private static string Version(string term) => term.StartsWith(">=") || term.StartsWith("<=") ? term.Substring(2)
        : term.StartsWith('=') || term.StartsWith('>') || term.StartsWith('<') ? term.Substring(1) : term;
    public static bool Matches(string version, string constraint)
    {
        if (!ValidConstraint(constraint)) { throw new FormatException("invalid dependency constraint"); }
        foreach (string term in constraint.Split(','))
        {
            int compare = Compare(version, Version(term));
            bool match = term.StartsWith(">=") ? compare >= 0 : term.StartsWith("<=") ? compare <= 0
                : term.StartsWith('>') ? compare > 0 : term.StartsWith('<') ? compare < 0 : compare == 0;
            if (!match) { return false; }
        }
        return true;
    }
}
