using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Zenith.Core.Configuration;
using Zenith.Core.Packages;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Shell.Commands;

internal static class PackageCommands
{
    public const string KeyPath = "/etc/zenith/package-key.pem";
    private static int s_busy;
    internal static bool IsRunning => Volatile.Read(ref s_busy) != 0;
    public static void Register(Action<Command> add)
    { add(new Command("pkg", "pkg mirror HTTPS_URL | refresh | search [TEXT] | plan NAME... | install [--yes] NAME... | upgrade [--yes] | kernel update [--yes] | repos/list/trust/check/remove/verify/recover", "Signed online and offline package management", Run)); }
    private static int Run(CommandContext c)
    {
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Interlocked.CompareExchange(ref s_busy, 1, 0) != 0)
            { return c.Fail("package/system update service busy"); }
        }
        try { int result = Execute(c); Interlocked.Exchange(ref s_busy, 0); return result; }
        catch { Interlocked.Exchange(ref s_busy, 0); throw; }
    }
    private static void Mutable()
    {
        GuardedFilesystem.RequireRoot();
        if (SystemMounts.Mode != BootMode.Installed || !new VfsFileMetadata().TryRead("/", out var stat) || !stat.UnixAttributes)
        { throw new IOException("package installation requires an installed ext2 root"); }
    }
    private static int Execute(CommandContext c)
    {
        if (c.Args.Length >= 2 && c.Args[0] == "kernel" && c.Args[1] == "update") { Mutable(); return KernelUpdate(c); }
        if (c.Args.Length > 0 && c.Args[0] == "kernel") { return Zenith.Core.Boot.NativeKernelUpdates.Package(c); }
        var store = new NativePackageStore(); var manager = new PackageManager(store);
        if (c.Args.Length == 2 && c.Args[0] == "mirror")
        { Mutable(); NativeRepositories.Mirror(c.Args[1]); c.WriteLine("core/extra mirror saved; run pkg refresh"); return 0; }
        if (c.Args.Length == 1 && c.Args[0] == "refresh")
        { Mutable(); NativeRepositories.Refresh(c); return 0; }
        if (c.Args.Length is 1 or 2 && c.Args[0] == "search")
        {
            foreach (var entry in NativeRepositories.Catalog().OrderBy(x => x.Name, StringComparer.Ordinal)
                .ThenByDescending(x => x.Version, Comparer<string>.Create(PackageVersions.Compare)))
            {
                if (c.Args.Length == 1 || entry.Name.Contains(c.Args[1], StringComparison.OrdinalIgnoreCase))
                { c.WriteLine(entry.Name + " " + entry.Version + " [" + entry.Repository + "; " + entry.Kind + "]"); }
            }
            return 0;
        }
        if (c.Args.Length >= 1 && c.Args[0] == "upgrade") { Mutable(); return Online(c, manager, true); }
        if (c.Args.Length >= 2 && c.Args[0] is "install" or "plan")
        {
            int offset = c.Args[1] == "--yes" ? 2 : 1;
            if (offset < c.Args.Length && c.Args.Skip(offset).All(PackageManifest.ValidName))
            {
                if (c.Args[0] == "install") { Mutable(); }
                return Online(c, manager, false);
            }
        }
        if (c.Args.Length == 1 && c.Args[0] == "repos")
        {
            foreach (var repo in PackageRepositories.Parse(BoundedFile.ReadText(PackageRepositories.Path, 4096) ?? PackageRepositories.Default))
            { c.WriteLine(repo.Name + " priority=" + repo.Priority + " mirror=" + (repo.Mirror ?? "not configured (offline)")); }
            return 0;
        }
        if (c.Args.Length == 1 && c.Args[0] == "list")
        {
            c.WriteLine("bundled image components:");
            foreach (var item in BootstrapComponents.All) { c.WriteLine(item.Name + " " + item.Version + " [" + item.Activation + "]"); }
            c.WriteLine("independently installed script/data packages:");
            foreach (var item in manager.List()) { c.WriteLine(item.Manifest.Name + " " + item.Manifest.Version + " [" + item.Manifest.Repository + "]"); }
            return 0;
        }
        if (c.Args.Length == 3 && c.Args[0] == "trust")
        {
            GuardedFilesystem.RequireRoot(); byte[] key = store.Read(c.Resolve(c.Args[1]), 4096);
            if (!PackagePlanner.Digest(key).Equals(c.Args[2], StringComparison.OrdinalIgnoreCase)) { throw new IOException("public key fingerprint mismatch"); }
            if (!NativePackageVerifier.ValidKey(key)) { throw new IOException("invalid RSA public key; existing trust preserved"); }
            string pem = new UTF8Encoding(false, true).GetString(key);
            if (!pem.StartsWith("-----BEGIN PUBLIC KEY-----\n", StringComparison.Ordinal) || !pem.TrimEnd().EndsWith("-----END PUBLIC KEY-----", StringComparison.Ordinal))
            { throw new IOException("expected public SPKI PEM; private keys refused"); }
            new NativeSettingsStore().Replace(KeyPath, pem); c.WriteLine("package public key pinned; TLS trust is separate"); return 0;
        }
        if (c.Args.Length >= 2 && c.Args[0] == "install")
        {
            Mutable(); var packages = new List<SignedPackage>(); byte[] key = store.Read(KeyPath, 4096);
            if (c.Args.Length > 17) { throw new IOException("at most 16 packages per transaction"); }
            foreach (string path in c.Args.AsSpan(1))
            { c.Shell.ThrowIfCancelled(); packages.Add(SignedPackage.Read(store.Read(c.Resolve(path), SignedPackage.MaximumBytes), key, new NativePackageVerifier())); }
            var progress = new CommandProgress(c.Out, c.IsTerminal);
            try { manager.Install(packages, c.Shell.ThrowIfCancelled, progress.Report); }
            catch { progress.Finish(); throw; }
            progress.Finish(); c.WriteLine("package transaction committed: " + packages.Count); return 0;
        }
        if (c.Args.Length >= 2 && c.Args[0] == "remove")
        { Mutable(); manager.Remove(c.Args.AsSpan(1).ToArray(), c.Shell.ThrowIfCancelled); c.WriteLine("packages removed=" + (c.Args.Length - 1)); return 0; }
        if (c.Args.Length == 1 && c.Args[0] == "recover")
        { Mutable(); manager.Recover(); c.WriteLine("package recovery complete"); return 0; }
        if (c.Args.Length == 1 && c.Args[0] == "verify")
        {
            var errors = manager.Verify(); foreach (string error in errors) { c.WriteLine(error); }
            c.WriteLine("package verification errors=" + errors.Count); return errors.Count == 0 ? 0 : 1;
        }
        if (c.Args.Length == 2 && c.Args[0] == "check")
        {
            var package = SignedPackage.Read(store.Read(c.Resolve(c.Args[1]), SignedPackage.MaximumBytes), store.Read(KeyPath, 4096), new NativePackageVerifier());
            c.WriteLine("verified signed package " + package.Manifest.Name + " " + package.Manifest.Version + " repo=" + package.Manifest.Repository); return 0;
        }
        if (c.Args.Length != 2 || !(c.Args[0] is "inspect" or "plan"))
        { return c.Fail("pkg repos/list/trust/install/remove/verify/recover/check; inspect/plan MANIFEST are unsigned dry-runs"); }
        string manifestPath = c.Resolve(c.Args[1]); var manifest = NativePackageSource.Load(manifestPath);
        c.WriteLine("experimental data package " + manifest.Name + " " + manifest.Version + " arch=" + manifest.Architecture);
        c.WriteLine("files=" + manifest.Files.Count + " payload=" + manifest.PayloadBytes + " runtime=" + manifest.RuntimeId);
        if (c.Args[0] == "inspect") { return 0; }
        var components = new List<BundledComponent>(BootstrapComponents.All);
        foreach (var item in manager.List()) { components.Add(new(item.Manifest.Name, item.Manifest.Version, "installed")); }
        var plan = PackagePlanner.Plan(manifest, new NativePackageSource(Path.GetDirectoryName(manifestPath) ?? "/"), "x86_64", components, NativePackageSource.State);
        foreach (string action in plan.Actions) { c.WriteLine(action); }
        foreach (string blocker in plan.Blockers) { c.WriteLine("blocked: " + blocker); }
        c.WriteLine("dry-run only; integrity hashes are not signatures; no files changed"); return plan.Applicable ? 0 : 1;
    }
    private static bool Confirm(CommandContext c, bool yes)
    {
        if (yes) { return true; }
        if (!c.IsTerminal || c.Input is not null || c.Shell.ReadInputLine is null)
        { throw new IOException("review pkg plan first, then supply --yes to apply non-interactively"); }
        string? response = c.Shell.ReadInputLine("Proceed [y/N]? "); c.Shell.ThrowIfCancelled();
        return response is "y" or "Y" or "yes";
    }
    private static int Online(CommandContext c, PackageManager manager, bool upgrade)
    {
        bool yes = c.Args.Length > 1 && c.Args[1] == "--yes";
        var catalog = NativeRepositories.Catalog(); var installed = manager.List();
        string[] names;
        if (upgrade)
        {
            if (c.Args.Length != (yes ? 2 : 1)) { return c.Fail("pkg upgrade [--yes]"); }
            names = installed.Where(x => catalog.Any(e => e.Kind == "data" && e.Name == x.Manifest.Name
                && PackageVersions.Compare(e.Version, x.Manifest.Version) > 0)).Select(x => x.Manifest.Name).ToArray();
            if (names.Length == 0) { c.WriteLine("no data package upgrades available"); return 0; }
        }
        else { names = c.Args.Skip(yes ? 2 : 1).ToArray(); }
        var plan = RepositoryPlanner.Plan(catalog, NativeRepositories.Repositories, installed, names, c.Shell.ThrowIfCancelled);
        foreach (var entry in plan) { c.WriteLine("Apply " + entry.Name + " " + entry.Version + " [" + entry.Repository + "] bytes=" + entry.Bytes); }
        if (plan.Count == 0) { c.WriteLine("packages already installed at the selected versions"); return 0; }
        if (c.Args[0] == "plan") { c.WriteLine("plan only; no downloads or installed changes"); return 0; }
        if (!Confirm(c, yes)) { c.WriteLine("package operation cancelled"); return 0; }
        _ = NativeRepositories.Catalog();
        var packages = new List<SignedPackage>();
        foreach (var entry in plan) { c.Shell.ThrowIfCancelled(); packages.Add(NativeRepositories.Data(entry, c)); }
        _ = NativeRepositories.Catalog();
        var progress = new CommandProgress(c.Out, c.IsTerminal);
        try { manager.Install(packages, c.Shell.ThrowIfCancelled, progress.Report); }
        catch { progress.Finish(); throw; }
        progress.Finish(); c.WriteLine("package transaction committed: " + packages.Count); return 0;
    }
    private static int KernelUpdate(CommandContext c)
    {
        bool yes = c.Args.Length == 3 && c.Args[2] == "--yes";
        if (c.Args.Length != (yes ? 3 : 2)) { return c.Fail("pkg kernel update [--yes]"); }
        var current = Zenith.Core.Boot.NativeKernelUpdates.ReadyForUpdate();
        var entry = NativeRepositories.Catalog().Where(x => x.Kind == "kernel" && PackageVersions.Compare(x.Version, current.HighWater) > 0)
            .OrderByDescending(x => x.Version, Comparer<string>.Create(PackageVersions.Compare)).FirstOrDefault();
        if (entry is null) { c.WriteLine("no newer kernel available"); return 0; }
        c.WriteLine("Stage zenith-kernel " + entry.Version + "; reboot and boot confirm required");
        if (!Confirm(c, yes)) { c.WriteLine("kernel update cancelled"); return 0; }
        _ = NativeRepositories.Catalog();
        string path = NativeRepositories.Download(entry, c);
        _ = NativeRepositories.Catalog();
        int result = Zenith.Core.Boot.NativeKernelUpdates.Package(new CommandContext(c.Shell, c.Name,
            new[] { "kernel", "install", path }, c.Input, c.Out, c.Err, c.IsTerminal));
        c.WriteLine("Kernel staged. Run reboot, then boot confirm after testing."); return result;
    }
}
