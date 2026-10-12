using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Cosmos.Kernel.Boot.Limine;
using Cosmos.Kernel.System.Vfs;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Packages;
using Zenith.Core.Security;
using Zenith.Core.Shell;
using Zenith.Core.Storage;

namespace Zenith.Core.Boot;

internal sealed class NativeKernelUpdateStore : IKernelUpdateStore
{
    private bool _recover;
    private readonly NativePackageStore _reader = new();
    public Action Cancel { get; set; } = () => { };
    public Action<string> Progress { get; set; } = _ => { };
    public bool Exists(string path) => NativePackageSource.State(path) != PackagePathState.Missing;
    public byte[] Read(string path, int maximum) => _reader.Read(path, maximum);
    public void RecoverWrites() => _recover = true;
    private static void Destination(string path)
    {
        GuardedFilesystem.RequireRoot();
        if (!(path.StartsWith("/boot/efi/boot/zenith/", StringComparison.Ordinal) || path == "/boot/efi/limine.conf"
            || path == "/boot/efi/boot/limine" || path == KernelUpdater.FallbackMenu || path.StartsWith("/var/lib/zenith/boot/", StringComparison.Ordinal)))
        { throw new IOException("kernel update destination refused"); }
        for (int slash = path.IndexOf('/', 1); slash >= 0; slash = path.IndexOf('/', slash + 1))
        { if (NativePackageSource.State(path[..slash]) != PackagePathState.Directory) { throw new IOException("linked/missing boot ancestor"); } }
    }
    public void Directory(string path)
    {
        GuardedFilesystem.RequireRoot();
        if (path != "/boot/efi/boot/limine" && !path.StartsWith("/boot/efi/boot/zenith/", StringComparison.Ordinal)) { throw new IOException("invalid boot directory"); }
        Destination(path);
        DirectoryCreate(path);
    }
    private static void DirectoryCreate(string path)
    {
        // Destination's final ancestor may be absent for the one new generation directory.
        System.IO.Directory.CreateDirectory(path);
    }
    public void Write(string path, byte[] bytes)
    {
        Destination(path); string temporary = path + ".zenith-new", backup = path + ".zenith-old";
        if (_recover)
        {
            if (Exists(backup) && !Exists(path) && !VfsManager.TryRename(backup, path)) { throw new IOException("cannot restore interrupted boot rename"); }
            if (Exists(backup)) { _ = Read(backup, KernelPackage.MaximumBytes); File.Delete(backup); }
            if (Exists(temporary)) { _ = Read(temporary, KernelPackage.MaximumBytes); File.Delete(temporary); }
        }
        if (Exists(temporary) || Exists(backup)) { throw new IOException("occupied boot staging path; run boot recover"); }
        if (Exists(path)) { _ = Read(path, KernelPackage.MaximumBytes); }
        if (!VfsManager.TryCreateFile(temporary, (VfsMode)0x1a4) || !VfsManager.TryOpenFile(temporary, out var file)) { throw new IOException("cannot stage boot file"); }
        try
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                if (path.EndsWith("/kernel.elf", StringComparison.Ordinal)) { Cancel(); }
                int length = Math.Min(65536, bytes.Length - offset);
                if (file.Write(bytes.AsSpan(offset, length)) != length) { throw new IOException("short boot update write"); }
                offset += length;
                if (path.EndsWith("/kernel.elf", StringComparison.Ordinal) && (offset % (1024 * 1024) == 0 || offset == bytes.Length))
                { Progress("kernel copy " + ((long)offset * 100 / bytes.Length) + "%"); }
            }
            if (!file.TryFlush()) { throw new IOException("boot update file flush failed"); }
            file.Dispose();
            if (!Read(temporary, KernelPackage.MaximumBytes).AsSpan().SequenceEqual(bytes)) { throw new IOException("boot update readback mismatch"); }
            Sync();
            if (Exists(path) && !VfsManager.TryRename(path, backup)) { throw new IOException("cannot retain boot file backup"); }
            Sync();
            if (path == KernelUpdater.MenuPath) { NativeKernelUpdates.TestCheckpoint("menu-gap"); }
            if (!VfsManager.TryRename(temporary, path)) { throw new IOException("cannot publish boot file"); }
            Sync();
            if (Exists(backup)) { File.Delete(backup); } Sync();
        }
        catch
        {
            file.Dispose();
            // Explicit cleanup: Cosmos does not reliably execute unwind-finally.
            if (Exists(backup) && !Exists(path)) { VfsManager.TryRename(backup, path); }
            if (Exists(temporary)) { File.Delete(temporary); }
            throw;
        }
    }
    public void Delete(string path)
    {
        GuardedFilesystem.RequireRoot();
        foreach (string file in _recover ? new[] { path, path + ".zenith-new", path + ".zenith-old" } : new[] { path })
        { if (Exists(file)) { Destination(path); _ = Read(file, KernelPackage.MaximumBytes); File.Delete(file); } }
    }
    public void Sync()
    {
        foreach (var mount in VfsManager.Mounts)
        {
            if ((mount.MountPoint == "/" || mount.MountPoint == "/boot/efi") && !mount.Superblock.SuperOperations.Sync(mount.Superblock))
            { throw new IOException("boot/root sync failed"); }
        }
        SystemMounts.RootPartition!.Host.Flush();
    }
    public ulong FreeEspBytes => VfsManager.TryStatFs("/boot/efi", out var stat) ? checked(stat.Bavail * stat.BlockSize) : throw new IOException("cannot determine EFI free space");
}

internal static class NativeKernelUpdates
{
    // Opt-in boot flags only used by disposable power-cut tests, never normal update behavior.
    internal static unsafe void TestCheckpoint(string phase)
    {
        string arguments = Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "";
        foreach (string token in arguments.Split(' '))
        {
            if (token != "kernel-test-stop=" + phase) { continue; }
            Log.StopPersisting(); new NativeKernelUpdateStore().Sync();
            // Serial only: persistent logging here would introduce an unflushed
            // root write between the tested sync boundary and guest termination.
            Log.WriteSerial("[zenith] kernel-update: power-cut checkpoint " + phase + "\n");
            while (true) { Thread.Sleep(100); }
        }
    }
    private static int s_busy;
    public static bool IsRunning => Volatile.Read(ref s_busy) != 0;
    private static KernelUpdater Open(NativeKernelUpdateStore store)
    {
        if (SystemMounts.Mode != BootMode.Installed || SystemMounts.RootPartition is null || SystemMounts.BootPartition is null || SystemMounts.RootId is null
            || !new VfsFileMetadata().TryRead("/", out var stat) || !stat.UnixAttributes) { throw new IOException("kernel updates require an installed ext2 root and validated EFI partition"); }
        Guid esp = Guid.Empty;
        foreach (var identity in GptIdentity.Read(SystemMounts.RootPartition.Host))
        { if (identity.Start == SystemMounts.BootPartition.StartSector && identity.Count == SystemMounts.BootPartition.BlockCount && identity.Type == GptIdentity.EspType) { esp = identity.Id; } }
        bool espMounted = false;
        foreach (var mount in VfsManager.Mounts)
        {
            const string metadata = "/var/lib/zenith/boot";
            bool metadataMount = mount.MountPoint != "/" && (mount.MountPoint == metadata
                || mount.MountPoint.StartsWith(metadata + "/", StringComparison.Ordinal)
                || metadata.StartsWith(mount.MountPoint.TrimEnd('/') + "/", StringComparison.Ordinal));
            if ((mount.MountPoint == "/boot/efi" && !ReferenceEquals(mount.Partition, SystemMounts.BootPartition))
                || mount.MountPoint.StartsWith("/boot/efi/", StringComparison.Ordinal) || metadataMount)
            { throw new IOException("unexpected mount in boot update paths"); }
            if (mount.MountPoint == "/boot/efi") { espMounted = true; }
        }
        if (!espMounted) { throw new IOException("validated EFI partition is not mounted at /boot/efi"); }
        return new(store, SystemMounts.RootId.Value, esp);
    }
    private static int Mutation(Func<int> operation, bool fromPackageService = false)
    {
        GuardedFilesystem.RequireRoot();
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || (!fromPackageService && Zenith.Core.Shell.Commands.PackageCommands.IsRunning) || Interlocked.CompareExchange(ref s_busy, 1, 0) != 0)
            { throw new IOException("system installation/update busy"); }
        }
        try { int result = operation(); Interlocked.Exchange(ref s_busy, 0); return result; }
        catch { Interlocked.Exchange(ref s_busy, 0); throw; }
    }
    public static int Package(CommandContext c)
    {
        if (c.Args.Length != 3 || c.Args[1] is not ("check" or "install")) { return c.Fail("pkg kernel check|install FILE.zpk"); }
        var progress = new CommandProgress(c.Out, c.IsTerminal);
        int Execute()
        {
            var store = new NativeKernelUpdateStore { Cancel = c.Shell.ThrowIfCancelled, Progress = progress.Report };
            var package = KernelPackage.Read(store.Read(c.Resolve(c.Args[2]), KernelPackage.MaximumBytes), store.Read(Zenith.Core.Shell.Commands.PackageCommands.KeyPath, 4096), new NativePackageVerifier());
            progress.Finish(); c.WriteLine("verified signed zenith-kernel " + package.Version + " sha256=" + package.Id);
            if (c.Args[1] == "install")
            {
                Open(store).Install(package, NativeBootAssets.RunningKernelId(), c.Shell.ThrowIfCancelled, text =>
                {
                    progress.Report(text);
                    if (text == "Publishing next-boot generation") { TestCheckpoint("staged"); }
                });
                // Also flush every other mounted volume before reporting success.
                // Normal power commands sync again and durably unmount the root.
                SystemMounts.SyncAll();
            }
            return 0;
        }
        try { int result = c.Args[1] == "check" ? Execute() : Mutation(Execute, true); progress.Finish(); return result; }
        catch { progress.Finish(); throw; }
    }
    public static int Boot(CommandContext c)
    {
        if (c.Args.Length != 1 || c.Args[0] is not ("status" or "confirm" or "rollback" or "recover")) { return c.Fail("boot status|confirm|rollback|recover"); }
        if (c.Args[0] == "status")
        {
            string running = NativeBootAssets.RunningKernelId(); c.WriteLine("running-kernel=" + running);
            c.WriteLine("root=" + (SystemMounts.RootId?.ToString("D") ?? "live/legacy")); c.WriteLine("efi=" + (SystemMounts.BootPartition?.Name ?? "unavailable"));
            if (SystemMounts.Mode == BootMode.Installed && SystemMounts.BootPartition is not null)
            {
                var store = new NativeKernelUpdateStore(); var state = Open(store).Status();
                c.WriteLine("next-kernel=" + state.Current.Id + " version=" + state.Current.Version);
                c.WriteLine("previous-kernel=" + (state.Previous?.Id ?? "none"));
                c.WriteLine("pending-confirmation=" + state.Pending.ToString().ToLowerInvariant());
                c.WriteLine("kernel-recovery-required=" + Open(store).RecoveryRequired.ToString().ToLowerInvariant());
                c.WriteLine("reboot-required=" + (running != state.Current.Id).ToString().ToLowerInvariant());
            }
            return 0;
        }
        return Mutation(() =>
        {
            var updater = Open(new NativeKernelUpdateStore());
            if (c.Args[0] == "recover") { updater.Recover(); }
            else if (c.Args[0] == "rollback") { updater.Rollback(); }
            else
            {
                if (Accounts.Error is not null) { throw new IOException("resolve account initialization failure before confirmation"); }
                if (new PackageManager(new NativePackageStore()).Verify().Count != 0) { throw new IOException("resolve package verification errors before confirmation"); }
                updater.Confirm(NativeBootAssets.RunningKernelId());
            }
            c.WriteLine("boot " + c.Args[0] + " complete"); return 0;
        });
    }
}
