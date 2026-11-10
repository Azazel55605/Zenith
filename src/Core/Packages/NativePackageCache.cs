using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Shell;
using Zenith.Core.Storage;
using Zenith.Core.Time;

namespace Zenith.Core.Packages;

/// <summary>The repository cache on the installed root: listing, cleanup and the download space check.
/// Every caller holds the package service gate (<c>PackageCommands</c>).</summary>
internal static class NativePackageCache
{
    public const string Path = "/var/lib/zenith/pkg/repositories";
    private static readonly NativePackageStore Store = new();
    public static long Now => (SystemClock.UtcNow.Ticks - new DateTime(1970, 1, 1).Ticks) / TimeSpan.TicksPerSecond;

    public static bool Automatic => PackageCachePolicy.AutomaticEnabled(BoundedFile.ReadText(PackageCachePolicy.ConfigPath, 256));
    public static void SetAutomatic(bool on) => new Zenith.Core.Configuration.NativeSettingsStore().Replace(PackageCachePolicy.ConfigPath, PackageCachePolicy.Config(on));

    /// <summary>The cache's entries, or none when it does not exist. A linked or non-directory cache
    /// path (or ancestor) is refused: cleanup never follows a link out of it.</summary>
    public static List<PackageCacheEntry> List()
    {
        var entries = new List<PackageCacheEntry>();
        string ancestor = "";
        foreach (string part in Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            ancestor += "/" + part;
            var state = Store.State(ancestor);
            if (state == PackagePathState.Missing) { return entries; }
            if (state != PackagePathState.Directory || !new VfsFileMetadata().TryRead(ancestor, out var stat) || stat.IsSymlink)
            { throw new IOException("package cache path is linked or not a directory: " + ancestor); }
        }
        foreach (string item in Directory.GetFileSystemEntries(Path))
        {
            string name = item.Substring(item.LastIndexOf('/') + 1);
            if (!new VfsFileMetadata().TryRead(Path + "/" + name, out var stat)) { continue; }
            entries.Add(new(name, stat.Type == "regular file", stat.IsSymlink, stat.Size, stat.Mtime));
        }
        return entries;
    }

    /// <summary>Removes what the policy allows; returns the plan and how many removals failed.</summary>
    public static PackageCachePlan Clean(PackageCacheMode mode, bool dryRun, out int failed)
    {
        var plan = PackageCachePolicy.Plan(List(), mode, Now);
        failed = 0;
        if (dryRun) { return plan; }
        foreach (var entry in plan.Remove)
        {
            try { Store.DeleteCached(Path + "/" + entry.Name); }
            catch (Exception error) { failed++; Log.Write("pkg", "cache cleanup kept " + entry.Name + ": " + error.Message); }
        }
        if (plan.Remove.Count > failed) { Store.Sync(); }
        return plan;
    }

    /// <summary>After a completed transaction (its archives are installed or staged): the automatic cleanup.</summary>
    public static void AfterTransaction(CommandContext c)
    {
        if (!Automatic) { return; }
        try
        {
            var plan = Clean(PackageCacheMode.All, false, out int failed);
            if (plan.Remove.Count > 0) { c.WriteLine("package cache: removed " + (plan.Remove.Count - failed) + " files, " + PackageCachePolicy.Human(plan.Bytes)); }
        }
        catch (Exception error) { c.WriteLine("package cache cleanup skipped: " + error.Message); }
    }

    /// <summary>Before downloading: room for the archives not cached yet, their installed payloads and a
    /// reserve on the root filesystem. Old archives are cleaned first when that makes room.</summary>
    public static void EnsureSpace(IEnumerable<RepositoryEntry> downloads, CommandContext c)
    {
        ulong needed = 0;
        foreach (var entry in downloads)
        {
            if (Store.State(Path + "/" + entry.Digest + ".zpk") == PackagePathState.Missing) { needed += (ulong)entry.Bytes; }
        }
        if (needed == 0) { return; }
        ulong required = PackageCachePolicy.Required(needed), free = Store.FreeBytes;
        if (free >= required) { return; }
        if (Automatic)
        {
            var plan = Clean(PackageCacheMode.All, false, out _);
            if (plan.Remove.Count > 0) { c.WriteLine("package cache: removed " + plan.Remove.Count + " files to make room, " + PackageCachePolicy.Human(plan.Bytes)); }
            free = Store.FreeBytes;
            if (free >= required) { return; }
        }
        throw new IOException("not enough space on / for this update: needs " + PackageCachePolicy.Human(required)
            + " (downloads " + PackageCachePolicy.Human(needed) + " plus installed files and reserve), "
            + PackageCachePolicy.Human(free) + " available. Run pkg clean, remove files, or enlarge the root partition.");
    }
}
