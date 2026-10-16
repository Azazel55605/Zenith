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
    public static void Refresh(CommandContext c)
    {
        Directories(); byte[] key = Key();
        foreach (var repository in Repositories)
        {
            if (repository.Mirror is null) { throw new IOException("set pkg mirror HTTPS_BASE_URL first"); }
            string legacy = Cache + "/" + repository.Name + ".zri";
            string cache = Cache + "/" + repository.Name + "-v3.zri", incoming = cache + ".incoming";
            var progress = new CommandProgress(c.Out, c.IsTerminal);
            try
            {
                c.WriteLine("Refreshing " + repository.Name);
                if (Store.State(incoming) != PackagePathState.Missing) { Store.Delete(incoming); }
                try
                {
                    NetworkService.Fetch(repository.Mirror.TrimEnd('/') + "/" + repository.Name + "/index-v3.zri", incoming,
                        false, c.Shell.ThrowIfCancelled, progress.Report, RepositoryIndex.MaximumBytes);
                }
                catch (Exception error)
                {
                    if (error.Message != "HTTP status 404" || Store.State(cache) != PackagePathState.Missing) { throw; }
                    cache = legacy; incoming = cache + ".incoming";
                    if (Store.State(incoming) != PackagePathState.Missing) { Store.Delete(incoming); }
                    NetworkService.Fetch(repository.Mirror.TrimEnd('/') + "/" + repository.Name + "/index.zri", incoming,
                        false, c.Shell.ThrowIfCancelled, progress.Report, RepositoryIndex.MaximumBytes);
                }
                byte[] bytes = Store.Read(incoming, RepositoryIndex.MaximumBytes);
                var index = RepositoryIndex.Read(bytes, key, Verifier, repository.Name); index.Fresh(Now);
                if (cache != legacy && Store.State(cache) == PackagePathState.Missing && Store.State(legacy) != PackagePathState.Missing)
                {
                    var floor = RepositoryIndex.Read(Store.Read(legacy, RepositoryIndex.MaximumBytes), key, Verifier, repository.Name);
                    if (index.Generation < floor.Generation) { throw new IOException("extended repository below legacy generation floor"); }
                }
                if (Store.State(cache) != PackagePathState.Missing)
                {
                    byte[] previous = Store.Read(cache, RepositoryIndex.MaximumBytes);
                    RepositoryIndex.Advance(RepositoryIndex.Read(previous, key, Verifier, repository.Name), previous, index, bytes);
                }
                c.Shell.ThrowIfCancelled();
                // This reserved cache stage can remain after a terminated refresh.
                // The prior signed snapshot/floor is retained until rename succeeds.
                if (Store.State(cache + ".pkg-new") != PackagePathState.Missing) { Store.Delete(cache + ".pkg-new"); }
                Store.Write(cache, bytes, 0x1a4); Store.Sync(); Store.Delete(incoming);
                progress.Finish(); c.WriteLine(repository.Name + " refreshed generation=" + index.Generation + " packages=" + index.Entries.Count);
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
            string cache = Cache + "/" + repository.Name + ".zri";
            if (Store.State(Cache + "/" + repository.Name + "-v3.zri") != PackagePathState.Missing) { cache = Cache + "/" + repository.Name + "-v3.zri"; }
            if (Store.State(cache) == PackagePathState.Missing) { throw new IOException("run pkg refresh first"); }
            var index = RepositoryIndex.Read(Store.Read(cache, RepositoryIndex.MaximumBytes), key, Verifier, repository.Name);
            index.Fresh(Now); entries.AddRange(index.Entries);
        }
        return entries.AsReadOnly();
    }
    public static string Download(RepositoryEntry entry, CommandContext c)
    {
        Directories(); string path = Cache + "/" + entry.Digest + ".zpk";
        if (Store.State(path) == PackagePathState.Missing)
        {
            var repository = Repositories.Single(x => x.Name == entry.Repository);
            var progress = new CommandProgress(c.Out, c.IsTerminal);
            try
            {
                c.WriteLine("Downloading " + entry.Name + " " + entry.Version);
                NetworkService.Fetch(repository.Mirror!.TrimEnd('/') + "/" + entry.ArchivePath, path, false,
                    c.Shell.ThrowIfCancelled, progress.Report, entry.Bytes); progress.Finish();
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
    public static SignedPackage Data(RepositoryEntry entry, CommandContext c)
    {
        var package = SignedPackage.Read(Store.Read(Download(entry, c), entry.Bytes), Key(), Verifier);
        if (package.ManifestText != entry.ManifestText) { throw new IOException("package manifest disagrees with signed index"); }
        return package;
    }
}
