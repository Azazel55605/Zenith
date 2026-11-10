using System;
using System.Collections.Generic;

namespace Zenith.Core.Packages;

/// <summary>One entry of the repository cache directory, as listed without following links.</summary>
internal readonly record struct PackageCacheEntry(string Name, bool RegularFile, bool Symlink, ulong Bytes, long ModifiedSeconds);

internal enum PackageCacheMode
{
    /// <summary><c>pkg clean</c> and after a completed transaction: every archive and abandoned stage.</summary>
    All,
    /// <summary>The periodic service: archives and stages older than their age limits only.</summary>
    Aged,
}

internal sealed record PackageCachePlan(IReadOnlyList<PackageCacheEntry> Remove, IReadOnlyList<PackageCacheEntry> Kept, ulong Bytes);

/// <summary>
/// What may leave the package cache. Only re-downloadable archives (<c>DIGEST.zpk</c>, named by
/// their signed SHA-256) and abandoned download/write stages (<c>.incoming</c>, <c>.pkg-new</c>)
/// are removed, and only regular files. Signed indexes (<c>.zri</c>) are anti-rollback floors and
/// stay, as does anything unknown, linked or not a regular file (reported, never removed).
/// Callers hold the package service gate, so no download or transaction owns a stage. Pure.
/// </summary>
internal static class PackageCachePolicy
{
    public const string ConfigPath = "/etc/zenith/package-cache.conf";
    /// <summary>The periodic service removes archives once they are a day old.</summary>
    public const long ArchiveAgeSeconds = 24 * 60 * 60;
    /// <summary>A stage left this long has no live owner (downloads and commits take seconds).</summary>
    public const long StageAgeSeconds = 60 * 60;
    /// <summary>Space kept free beyond the downloads and their installed payloads.</summary>
    public const ulong ReserveBytes = 4UL * 1024 * 1024;

    public static bool IsArchive(string name)
    {
        if (name.Length != 68 || !name.EndsWith(".zpk", StringComparison.Ordinal)) { return false; }
        for (int i = 0; i < 64; i++)
        {
            char c = name[i];
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) { return false; }
        }
        return true;
    }

    public static bool IsStage(string name)
        => name.Length > 9 && name.IndexOf('/') < 0
            && (name.EndsWith(".incoming", StringComparison.Ordinal) || name.EndsWith(".pkg-new", StringComparison.Ordinal));

    public static PackageCachePlan Plan(IEnumerable<PackageCacheEntry> entries, PackageCacheMode mode, long nowSeconds)
    {
        var remove = new List<PackageCacheEntry>(); var kept = new List<PackageCacheEntry>(); ulong bytes = 0;
        foreach (var entry in entries)
        {
            bool archive = IsArchive(entry.Name), stage = !archive && IsStage(entry.Name);
            long age = nowSeconds - entry.ModifiedSeconds;
            bool old = mode == PackageCacheMode.All
                || (archive && age >= ArchiveAgeSeconds) || (stage && age >= StageAgeSeconds);
            if ((archive || stage) && entry.RegularFile && !entry.Symlink && old)
            {
                remove.Add(entry); bytes += entry.Bytes;
            }
            else if (entry.Symlink || !entry.RegularFile || !(archive || stage || entry.Name.EndsWith(".zri", StringComparison.Ordinal)))
            {
                // Young archives/stages and indexes are expected; anything else kept is reported.
                kept.Add(entry);
            }
        }
        return new PackageCachePlan(remove.AsReadOnly(), kept.AsReadOnly(), bytes);
    }

    /// <summary>Whether automatic cleanup is on: the default, unless the file says <c>auto=off</c>.</summary>
    public static bool AutomaticEnabled(string? config)
    {
        if (config is null) { return true; }
        string[] fields = config.Trim().Split(' ');
        return !(fields.Length == 3 && fields[0] == "ZENITH-PKG-CACHE" && fields[1] == "1" && fields[2] == "auto=off");
    }

    public static string Config(bool automatic) => "ZENITH-PKG-CACHE 1 auto=" + (automatic ? "on" : "off") + "\n";

    /// <summary>Bytes a download set needs free: the archives, their installed payloads, and a reserve.</summary>
    public static ulong Required(ulong downloadBytes) => downloadBytes * 2 + ReserveBytes;

    public static string Human(ulong bytes)
        => bytes >= 1UL << 30 ? (bytes / (double)(1UL << 30)).ToString("0.0") + " GiB"
            : bytes >= 1UL << 20 ? (bytes / (double)(1UL << 20)).ToString("0.0") + " MiB"
            : bytes >= 1UL << 10 ? (bytes / 1024.0).ToString("0.0") + " KiB" : bytes + " B";
}
