using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zenith.Core.Configuration;
using Zenith.Core.Network;
using Zenith.Core.Shell;
using Zenith.Core.Shell.Commands;
using Zenith.Core.Time;

namespace Zenith.Core.Packages;

/// <summary>Where a package service step reports: the shell's command output, or an Orbit job's status.</summary>
internal sealed class PackageReporter
{
    public Action Cancel = () => { };
    public Action<string> Say = _ => { }, Progress = _ => { };
    public Action Finish = () => { };
    public static PackageReporter For(CommandContext c)
    {
        var progress = new CommandProgress(c.Out, c.IsTerminal);
        return new() { Cancel = c.Shell.ThrowIfCancelled, Say = c.WriteLine, Progress = progress.Report, Finish = progress.Finish };
    }
}

internal static class NativeRepositories
{
    private const string Cache = "/var/lib/zenith/pkg/repositories";
    private static readonly NativePackageStore Store = new();
    private static readonly NativePackageVerifier Verifier = new();
    public static IReadOnlyList<PackageRepository> Repositories => PackageRepositories.Parse(
        Zenith.Core.Storage.BoundedFile.ReadText(PackageRepositories.Path, 4096) ?? PackageRepositories.Default);
    private static long Now => (SystemClock.UtcNow.Ticks - new DateTime(1970, 1, 1).Ticks) / TimeSpan.TicksPerSecond;
    private static byte[] Key()
    {
        if (Store.State(PackageCommands.KeyPath) == PackagePathState.Missing)
        { throw new IOException("package trust missing; root must run pkg trust FILE SHA256"); }
        return Store.Read(PackageCommands.KeyPath, 4096);
    }
    public static void Mirror(string url)
    {
        string text = "ZENITH-REPOS\t1\ncore\t10\t" + url.TrimEnd('/') + "\nextra\t20\t" + url.TrimEnd('/') + "\n";
        _ = PackageRepositories.Parse(text); new NativeSettingsStore().Replace(PackageRepositories.Path, text);
    }
    private static void Directories()
    {
        foreach (string directory in new[] { "/var", "/var/lib", "/var/lib/zenith", PackageManager.Root, Cache })
        {
            if (Store.State(directory) == PackagePathState.Missing) { Store.Directory(directory); }
            if (Store.State(directory) != PackagePathState.Directory) { throw new IOException("occupied repository cache directory"); }
        }
    }
    private static readonly string[] CatalogVersions = { "-v4", "-v3", "" };
    private static string CatalogPath(string name, string version) => Cache + "/" + name + version + ".zri";
    internal static string CachedCatalog(string name)
    {
        foreach (string version in CatalogVersions)
        {
            string path = CatalogPath(name, version);
            if (Store.State(path) != PackagePathState.Missing) { return path; }
        }
        return CatalogPath(name, "");
    }

    public static void Refresh(CommandContext c) => Refresh(PackageReporter.For(c));
    public static void Refresh(PackageReporter c)
    {
        Directories(); byte[] key = Key();
        foreach (var repository in Repositories)
        {
            if (repository.Mirror is null) { throw new IOException("set pkg mirror HTTPS_BASE_URL first"); }
            string cache = "", incoming = "";
            var progress = c;
            try
            {
                c.Say("Refreshing " + repository.Name);
                foreach (string version in CatalogVersions)
                {
                    cache = CatalogPath(repository.Name, version);
                    incoming = cache + ".incoming";
                    if (Store.State(incoming) != PackagePathState.Missing) { Store.Delete(incoming); }
                    try
                    {
                        NetworkService.Fetch(repository.Mirror.TrimEnd('/') + "/" + repository.Name + "/index" + version + ".zri",
                            incoming, false, c.Cancel, progress.Progress, RepositoryIndex.MaximumBytes);
                        break;
                    }
                    catch (Exception error) when (version.Length != 0 && error.Message == "HTTP status 404"
                        && Store.State(cache) == PackagePathState.Missing) { }
                }
                byte[] bytes = Store.Read(incoming, RepositoryIndex.MaximumBytes);
                var index = RepositoryIndex.Read(bytes, key, Verifier, repository.Name); index.Fresh(Now);
                foreach (string version in CatalogVersions)
                {
                    string path = CatalogPath(repository.Name, version);
                    if (Store.State(path) == PackagePathState.Missing) { continue; }
                    byte[] previous = Store.Read(path, RepositoryIndex.MaximumBytes);
                    var floor = RepositoryIndex.Read(previous, key, Verifier, repository.Name);
                    if (cache == path) { RepositoryIndex.Advance(floor, previous, index, bytes); }
                    else if (index.Generation < floor.Generation) { throw new IOException("repository below prior catalog generation floor"); }
                }
                c.Cancel();
                // This reserved cache stage can remain after a terminated refresh.
                // The prior signed snapshot/floor is retained until rename succeeds.
                if (Store.State(cache + ".pkg-new") != PackagePathState.Missing) { Store.Delete(cache + ".pkg-new"); }
                Store.Write(cache, bytes, 0x1a4); Store.Sync(); Store.Delete(incoming);
                progress.Finish(); c.Say(repository.Name + " refreshed generation=" + index.Generation + " packages=" + index.Entries.Count);
            }
            catch
            {
                progress.Finish(); try { Store.Delete(incoming); } catch { } throw;
            }
        }
    }
    public static IReadOnlyList<RepositoryEntry> Catalog()
    {
        byte[] key = Key(); var entries = new List<RepositoryEntry>();
        foreach (var repository in Repositories)
        {
            if (repository.Mirror is null) { throw new IOException("set pkg mirror HTTPS_BASE_URL first"); }
            string cache = CachedCatalog(repository.Name);
            if (Store.State(cache) == PackagePathState.Missing) { throw new IOException("run pkg refresh first"); }
            var index = RepositoryIndex.Read(Store.Read(cache, RepositoryIndex.MaximumBytes), key, Verifier, repository.Name);
            index.Fresh(Now); entries.AddRange(index.Entries);
        }
        return entries.AsReadOnly();
    }
    public static string Download(RepositoryEntry entry, CommandContext c) => Download(entry, PackageReporter.For(c));
    public static string Download(RepositoryEntry entry, PackageReporter c)
    {
        Directories(); string path = Cache + "/" + entry.Digest + ".zpk";
        if (Store.State(path) == PackagePathState.Missing)
        {
            var repository = Repositories.Single(x => x.Name == entry.Repository);
            var progress = c;
            try
            {
                c.Say("Downloading " + entry.Name + " " + entry.Version);
                NetworkService.Fetch(repository.Mirror!.TrimEnd('/') + "/" + entry.ArchivePath, path, false,
                    c.Cancel, progress.Progress, entry.Bytes); progress.Finish();
            }
            catch { progress.Finish(); throw; }
        }
        byte[] bytes = Store.Read(path, entry.Bytes);
        if (bytes.Length != entry.Bytes || PackagePlanner.Digest(bytes) != entry.Digest)
        { File.Delete(path); throw new IOException("archive disagrees with signed index; bad cache removed"); }
        if (entry.Kind == "kernel" && Zenith.Core.Boot.KernelPackage.Read(bytes, Key(), Verifier).Version != entry.Version)
        { throw new IOException("kernel version disagrees with signed index"); }
        return path;
    }
    public static List<SignedPackage> LoadData(IReadOnlyList<RepositoryEntry> entries, PackageReporter reporter)
    {
        long bytes = 0;
        foreach (var entry in entries)
        {
            bytes += PackageManifest.Parse(entry.ManifestText).PayloadBytes;
            PackageManager.CheckTransactionBytes(bytes);
        }
        var packages = new List<SignedPackage>(entries.Count);
        foreach (var entry in entries) { reporter.Cancel(); packages.Add(Data(entry, reporter)); }
        return packages;
    }
    public static SignedPackage Data(RepositoryEntry entry, CommandContext c) => Data(entry, PackageReporter.For(c));
    public static SignedPackage Data(RepositoryEntry entry, PackageReporter c)
    {
        var package = SignedPackage.Read(Store.Read(Download(entry, c), entry.Bytes), Key(), Verifier);
        if (package.ManifestText != entry.ManifestText) { throw new IOException("package manifest disagrees with signed index"); }
        return package;
    }
}
