using Zenith.Core.Security;
using Zenith.Core.Boot;
using System.Runtime.InteropServices;
using Cosmos.Kernel.Boot.Limine;
using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Filesystems.Fat;
using Zenith.Core.Storage.Ext2;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Storage.Proc;
using Zenith.Core.Storage.Dev;

namespace Zenith.Core.Storage;

internal enum BootMode
{
    /// <summary>Root is a partition carrying a Zenith installation; changes persist.</summary>
    Installed,

    /// <summary>No installation found: root is a RAM disk, like a live CD. Changes are lost on reboot.</summary>
    Live,
}

/// <summary>
/// Builds the mount table at boot, Unix style: one root filesystem at <c>/</c>, with
/// <c>/tmp</c> (always RAM) mounted on top of it.
/// </summary>
internal static class SystemMounts
{
    /// <summary>Driver name for on-disk FAT volumes, as used by <c>mount</c>.</summary>
    public const string Fat = "fat";
    public const string Ext2 = "ext2";

    private const ulong SectorSize = 512;
    private static readonly object s_mountGate = new();

    public static BootMode Mode { get; private set; }

    /// <summary>The partition mounted at <c>/</c> when <see cref="Mode"/> is <see cref="BootMode.Installed"/>.</summary>
    public static Partition? RootPartition { get; private set; }

    public static Partition? BootPartition { get; private set; }
    public static Guid? RootId { get; private set; }
    public static BootRootSelection Selection { get; private set; } = new(false, null, null);

    public static unsafe void Initialize()
    {
        VfsManager.RegisterFilesystem(Fat, new GuardedFilesystem(new FatFilesystemType()));
        VfsManager.RegisterFilesystem(Ext2, new GuardedFilesystem(new Ext2FilesystemType()));

        Selection = BootRootSelection.Parse(Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "");
        if (Selection.KernelId is not null && NativeBootAssets.RunningKernelId() != Selection.KernelId)
        { throw new IOException("selected kernel generation does not match loaded bytes"); }
        RootPartition = Selection.Live ? null : FindInstalledRoot(Selection.PartitionId);
        if (Selection.PartitionId is not null && RootPartition is null)
        { throw new IOException("requested root UUID is unavailable or invalid; automatic fallback refused"); }
        if (RootPartition is not null)
        {
            Mode = BootMode.Installed;
            Log.Write("mounts", "root: " + RootPartition.Name + " (installed)");
        }
        else
        {
            Mode = BootMode.Live;
            MountRamDisk("rootfs", 64, "/");
            Log.Write("mounts", "root: RAM disk (live)");
        }

        FileSystemLayout.Create();
        if (RootPartition is not null) { MountBootPartition(); }
        Log.PersistTo("/var/log/boot.log");
        MountRamDisk("tmpfs", 16, "/tmp");
        ProcMount.Initialize();
        DevMount.Initialize();
    }

    /// <summary>The partition's GPT unique GUID (what /etc/fstab names it by, PARTUUID=); null
    /// on a disk without a valid GPT.</summary>
    public static Guid? PartitionId(Partition partition)
    {
        try
        {
            foreach (var identity in GptIdentity.Read(partition.Host))
            { if (identity.Start == partition.StartSector && identity.Count == partition.BlockCount) { return identity.Id; } }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>/etc/fstab as it is now (empty when there is none). Read fresh every time: people
    /// edit the file by hand too, so Zenith never works from an old copy.</summary>
    public static List<FstabLine> ReadFstab() => Fstab.Parse(BoundedFile.ReadText(Fstab.Path, 16384));

    /// <summary>Changes /etc/fstab through <paramref name="edit"/>, which sees the current lines and
    /// returns the new ones. If the file changed between reading and replacing it (saved from an
    /// editor meanwhile), the edit is made again on the new text, so neither change is lost.</summary>
    public static void EditFstab(Func<List<FstabLine>, List<FstabLine>> edit)
    {
        GuardedFilesystem.RequireRoot();
        lock (s_fstabGate)
        {
            for (int attempt = 0; ; attempt++)
            {
                string? before = BoundedFile.ReadText(Fstab.Path, 16384);
                var lines = edit(Fstab.Parse(before));
                string after = Fstab.Serialize(lines);
                if (before is null || before.Length == 0) { after = Fstab.NewFileHeader + after; }
                if (BoundedFile.ReadText(Fstab.Path, 16384) != before)
                {
                    if (attempt < 3) { continue; }
                    throw new IOException("/etc/fstab keeps changing; try again when it is saved");
                }
                if (after == (before ?? "")) { return; }
                new Zenith.Core.Configuration.NativeSettingsStore().Replace(Fstab.Path, after);
                return;
            }
        }
    }
    private static readonly object s_fstabGate = new();

    /// <summary>Mounts what /etc/fstab lists (installed systems; after accounts load, since file
    /// access checks need them). A line Zenith does not understand, a partition that is missing or a
    /// check that fails is logged and skipped: a second disk never stops the boot.</summary>
    public static void MountAtStartup()
    {
        if (RootPartition is null) { return; }
        List<FstabLine> lines;
        try
        {
            if (!new VfsFileMetadata().TryRead(Fstab.Path, out _)) { Log.Write("mounts", "no /etc/fstab; nothing to mount at startup"); return; }
            lines = ReadFstab();
        }
        catch (Exception e) { Log.Write("mounts", "/etc/fstab skipped: " + e.Message); return; }
        foreach (var line in lines)
        {
            if (line.Problem is string problem) { Log.Write("mounts", "/etc/fstab: skipped \"" + line.Text.Trim() + "\": " + problem); continue; }
            if (line.Entry is not FstabEntry entry || entry.NoAuto || entry.System) { continue; }
            try
            {
                Partition? partition = null;
                foreach (Partition candidate in StorageManager.Partitions)
                {
                    if (entry.Device is string device ? candidate.Name == device : PartitionId(candidate) == entry.Partition) { partition = candidate; break; }
                }
                if (partition is null) { Log.Write("mounts", entry.MountPoint + ": partition is not attached; skipped"); continue; }
                if (!Directory.Exists(entry.MountPoint))
                {
                    // Zenith makes its own mount points under /mnt; anywhere else the directory must exist.
                    if (!entry.MountPoint.StartsWith("/mnt/", StringComparison.Ordinal)) { Log.Write("mounts", entry.MountPoint + ": mount point does not exist; skipped"); continue; }
                    Directory.CreateDirectory(entry.MountPoint);
                }
                string filesystem = entry.Filesystem;
                if (filesystem == "auto")
                {
                    var probed = DiskService.Probe(partition).Filesystem;
                    filesystem = probed == DiskFilesystem.Ext2 ? Ext2 : probed == DiskFilesystem.Fat32 ? Fat : "";
                    if (filesystem.Length == 0) { Log.Write("mounts", entry.MountPoint + ": no ext2 or FAT filesystem on " + partition.Name + "; skipped"); continue; }
                }
                string? error;
                lock (s_mountGate) { error = MountCore(partition.Name, entry.MountPoint, filesystem); }
                Log.Write("mounts", entry.MountPoint + ": " + (error ?? "mounted " + partition.Name + " (" + filesystem + ") at startup"));
            }
            catch (Exception e) { Log.Write("mounts", entry.MountPoint + ": " + e.Message); }
        }
    }

    /// <summary>Flushes every mounted filesystem to its disk (the <c>sync</c> command). Returns how many were synced.</summary>
    public static int SyncAll()
    {
        int synced = 0;
        foreach (VfsManager.VfsMount mount in VfsManager.Mounts)
        {
            if (!mount.Superblock.SuperOperations.Sync(mount.Superblock))
            { throw new IOException("filesystem sync failed: " + mount.MountPoint); }
            synced++;
        }

        return synced;
    }

    /// <summary>
    /// Mounts a FAT or explicitly selected ext2 partition (by name, e.g. <c>sata0p1</c>) on an existing directory.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Mount(string partitionName, string mountPoint, string filesystem = Fat)
    {
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || NativeKernelUpdates.IsRunning || Shell.Commands.PackageCommands.IsRunning) { return "installation/update busy; mount changes refused"; }
            lock (s_mountGate) { return MountCore(partitionName, mountPoint, filesystem); }
        }
    }

    private static string? MountCore(string partitionName, string mountPoint, string filesystem)
    {
        GuardedFilesystem.RequireRoot();
        if (filesystem != Fat && filesystem != Ext2)
        {
            return filesystem + ": unsupported filesystem (use fat or ext2)";
        }
        Partition? partition = null;
        foreach (Partition candidate in StorageManager.Partitions)
        {
            if (candidate.Name == partitionName)
            {
                partition = candidate;
            }
        }

        if (partition is null)
        {
            return partitionName + ": no such partition (see lsblk)";
        }

        foreach (VfsManager.VfsMount existing in VfsManager.Mounts)
        {
            if (ReferenceEquals(existing.Partition, partition))
            {
                return partitionName + " is already mounted on " + existing.MountPoint;
            }

            if (existing.MountPoint == mountPoint)
            {
                return mountPoint + " is already a mount point";
            }
        }

        if (!Directory.Exists(mountPoint))
        {
            return mountPoint + ": mount point does not exist";
        }

        if (filesystem == Ext2 && Ext2VolumePolicy.Check(partition) is string error)
        {
            return partitionName + ": " + error;
        }
        return VfsManager.TryMount(filesystem, partition, MountFlags.None, mountPoint, out _)
            ? null : partitionName + ": could not mount " + filesystem + " filesystem";
    }

    public static bool CheckExt2(string name, out string report)
    {
        GuardedFilesystem.RequireRoot();
        lock (s_mountGate)
        {
            foreach (Partition partition in StorageManager.Partitions)
            {
                if (partition.Name != name) { continue; }
                foreach (var mount in VfsManager.Mounts)
                {
                    if (mount.Partition is Partition used && ReferenceEquals(used.Host, partition.Host)
                        && used.StartSector < partition.StartSector + partition.BlockCount
                        && partition.StartSector < used.StartSector + used.BlockCount)
                    {
                        report = "refusing to check a mounted partition; unmount it first"; return false;
                    }
                }
                return Ext2Checker.Check(partition, out report);
            }
            report = "no such partition (see lsblk)"; return false;
        }
    }

    /// <summary>Formats an explicitly selected, unmounted secondary partition.</summary>
    public static string? FormatExt2(string name, string label, bool confirmed)
    {
        GuardedFilesystem.RequireRoot();
        lock (s_mountGate)
        {
            Partition? target = null;
            foreach (Partition partition in StorageManager.Partitions)
            {
                if (partition.Name == name)
                {
                    target = partition;
                }
            }
            if (target is null)
            {
                return name + ": no such partition (whole disks cannot be formatted here)";
            }
            if (ReferenceEquals(target.Host, RootPartition?.Host))
            {
                return "refusing to format a partition on the running root disk";
            }
            foreach (VfsManager.VfsMount mount in VfsManager.Mounts)
            {
                Partition? used = mount.Partition;
                if (used is not null && ReferenceEquals(used.Host, target.Host)
                    && used.StartSector < target.StartSector + target.BlockCount
                    && target.StartSector < used.StartSector + used.BlockCount)
                {
                    return name + " overlaps a partition mounted on " + mount.MountPoint;
                }
            }
            if (Ext2Formatter.Check(target, label) is string error)
            {
                return error;
            }
            if (!confirmed)
            {
                return "formatting erases " + name + "; pass --yes to confirm";
            }
            Ext2Formatter.Format(target, label);
            Log.Write("mkfs", "formatted " + name + " as ext2");
            return null;
        }
    }

    /// <summary>Unmounts one mount point (never the root). Returns an error message, or null on success.</summary>
    public static string? Unmount(string mountPoint)
    {
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || NativeKernelUpdates.IsRunning || Shell.Commands.PackageCommands.IsRunning) { return "installation/update busy; mount changes refused"; }
            lock (s_mountGate) { return UnmountCore(mountPoint); }
        }
    }

    private static string? UnmountCore(string mountPoint)
    {
        GuardedFilesystem.RequireRoot();
        if (mountPoint == "/")
        {
            return "/: the root filesystem stays mounted while the system runs";
        }

        foreach (VfsManager.VfsMount mount in VfsManager.Mounts)
        {
            if (mount.MountPoint.StartsWith(mountPoint + "/"))
            {
                return mountPoint + ": target is busy (" + mount.MountPoint + " is mounted below it)";
            }
        }

        return VfsManager.TryUnmount(mountPoint) ? null : mountPoint + ": not mounted";
    }

    /// <summary>
    /// The shutdown sequence: stop writing the log file, sync, then unmount every filesystem
    /// deepest-first (unmounting flushes) and flush the disks themselves.
    /// </summary>
    public static void Shutdown()
    {
        GuardedFilesystem.RequireRoot();
        Log.StopPersisting();
        SyncAll();

        var mounts = new List<VfsManager.VfsMount>(VfsManager.Mounts);
        mounts.Sort((a, b) => b.MountPoint.Length.CompareTo(a.MountPoint.Length));
        foreach (VfsManager.VfsMount mount in mounts)
        {
            if (!VfsManager.TryUnmount(mount.MountPoint))
            { throw new IOException("filesystem unmount failed: " + mount.MountPoint); }
        }

        foreach (IBlockDevice device in StorageManager.Devices)
        {
            device.Flush();
        }

        Log.Write("mounts", "all filesystems unmounted");
    }

    /// <summary>Mounts every partition in turn at <c>/</c> and keeps the first one carrying a Zenith installation.</summary>
    private static Partition? FindInstalledRoot(Guid? selected)
    {
        Partition? exact = null;
        if (selected is not null)
        {
            foreach (Partition candidate in StorageManager.Partitions)
            {
                IReadOnlyList<GptIdentity> identities;
                try { identities = GptIdentity.Read(candidate.Host); }
                catch (IOException) { continue; /* Invalid GPT cannot satisfy an explicit selector. */ }
                // Keep ambiguity rejection outside a typed catch: Cosmos NativeAOT
                // currently misclassifies some typed exception handlers.
                foreach (var identity in identities)
                {
                    if (identity.Id == selected && identity.Start == candidate.StartSector && identity.Count == candidate.BlockCount)
                    {
                        if (exact is not null) { throw new InvalidOperationException("ambiguous root UUID"); }
                        exact = candidate;
                    }
                }
            }
            if (exact is null) { return null; }
        }
        foreach (Partition partition in StorageManager.Partitions)
        {
            if (selected is not null && !ReferenceEquals(partition, exact)) { continue; }
            bool mounted = Ext2VolumePolicy.Check(partition, allowUnclean: true) is null
                && VfsManager.TryMount(Ext2, partition, MountFlags.None, "/", out _);
            if (!mounted && !VfsManager.TryMount(Fat, partition, MountFlags.None, "/", out _))
            {
                continue;
            }

            try
            {
                if (FileSystemLayout.IsZenithRoot())
                {
                    return partition;
                }
            }
            catch (Exception)
            {
                // A damaged volume is just not a candidate.
            }

            VfsManager.TryUnmount("/");
        }

        return null;
    }

    private static void MountBootPartition()
    {
        try
        {
            var identities = GptIdentity.Read(RootPartition!.Host);
            foreach (var identity in identities)
            {
                if (identity.Start == RootPartition.StartSector && identity.Count == RootPartition.BlockCount) { RootId = identity.Id; }
                if (identity.Type != GptIdentity.EspType) { continue; }
                foreach (var partition in StorageManager.Partitions)
                {
                    if (ReferenceEquals(partition.Host, RootPartition.Host) && partition.StartSector == identity.Start && partition.BlockCount == identity.Count)
                    {
                        if (BootPartition is not null) { throw new IOException("multiple EFI partitions on root disk"); }
                        BootPartition = partition;
                    }
                }
            }
            if (BootPartition is not null && !VfsManager.TryMount(Fat, BootPartition, MountFlags.None, "/boot/efi", out _))
            { throw new IOException("could not mount installed EFI partition"); }
            Log.Write("mounts", BootPartition is null ? "EFI partition unavailable (legacy root)" : "EFI partition: " + BootPartition.Name + " at /boot/efi");
        }
        catch (IOException)
        {
            if (Selection.PartitionId is not null) { throw; }
            BootPartition = null; RootId = null;
            Log.Write("mounts", "legacy root has no validated boot partition identity");
        }
    }

    private static void MountRamDisk(string name, ulong megabytes, string mountPoint)
    {
        var device = new MemoryBlockDevice(name, SectorSize, megabytes * 1024 * 1024 / SectorSize);
        if (mountPoint == "/tmp")
        {
            if (!VfsManager.RegisterFilesystem(name, new GuardedFilesystem(new RamExt2FilesystemType(device)))
                || !VfsManager.TryFormat(name, "", null)
                || !VfsManager.TryMount(name, "", MountFlags.None, mountPoint, out _)
                || !new VfsFileMetadata().SetMode(mountPoint, 0x3ff))
            { throw new IOException("could not mount private RAM tmp filesystem"); }
            return;
        }
        if (!VfsManager.RegisterFilesystem(name, new GuardedFilesystem(new FatFilesystemType(device)))
            || !VfsManager.TryFormat(name, "", new FatFormatOptions { Type = FatType.Fat16 })
            || !VfsManager.TryMount(name, "", MountFlags.None, mountPoint, out _))
        {
            Log.Write("mounts", "failed to mount RAM disk " + name + " at " + mountPoint);
        }
    }
}
