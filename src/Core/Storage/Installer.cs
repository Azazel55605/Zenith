using System;
using Zenith.Core.Boot;
using Zenith.Core.Security;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Filesystems.Fat;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Core.Storage;

/// <summary>Self-contained x64 UEFI installation with versioned kernel artifacts.</summary>
internal static class Installer
{
    internal static readonly object MaintenanceGate = new();
    private static int s_running;
    public static bool IsRunning => Volatile.Read(ref s_running) != 0;

    public const ulong MinimumBytes = BootLayout.MinimumDiskBytes;

    private const ulong EspBytes = BootLayout.EspBytes;
    private const ulong Alignment = 2048;   // 1 MiB in 512-byte sectors
    private const string TargetMount = "/mnt/target";
    private const string EspMount = "/mnt/esp";

    /// <summary>GPT type of an EFI System Partition: C12A7328-F81F-11D2-BA4B-00A0C93EC93B.</summary>
    private static readonly Guid EfiSystemPartitionType =
        new(0xC12A7328, 0xF81F, 0x11D2, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B);

    private static readonly Guid LinuxFilesystemType = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    private static readonly string[] s_copiedTrees = { "/etc", "/home", "/root", "/usr", "/var" };

    /// <summary>Disks Zenith could be installed on: big enough, and not the disk the running root lives on.</summary>
    public static List<IBlockDevice> Candidates()
    {
        var disks = new List<IBlockDevice>();
        foreach (IBlockDevice disk in StorageManager.Devices)
        {
            if (disk.BlockCount * disk.BlockSize >= MinimumBytes && !ReferenceEquals(disk, SystemMounts.RootPartition?.Host))
            {
                disks.Add(disk);
            }
        }

        return disks;
    }

    /// <summary>Erases <paramref name="disk"/> and installs onto it. Throws on failure.</summary>
    public static void Install(IBlockDevice disk, Action<string> progress, InstallationProfile? profile = null)
    {
        RunJob(disk, progress, () => profile);
    }

    public static void InstallConfigured(IBlockDevice disk, Action<string> progress, Func<InstallationProfile> prepare)
        => RunJob(disk, progress, prepare);

    private static void RunJob(IBlockDevice disk, Action<string> progress, Func<InstallationProfile?> prepare)
    {
        GuardedFilesystem.RequireRoot();
        lock (MaintenanceGate)
        {
            if (Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning || Interlocked.CompareExchange(ref s_running, 1, 0) != 0)
            { throw new IOException("installation or package update already running"); }
        }
        try { InstallCore(disk, progress, prepare()); }
        catch { Interlocked.Exchange(ref s_running, 0); throw; }
        Interlocked.Exchange(ref s_running, 0);
    }

    private static void InstallCore(IBlockDevice disk, Action<string> progress, InstallationProfile? profile)
    {
        if (profile is not null && SystemMounts.Mode != BootMode.Live)
        { throw new IOException("initial account setup requires the ISO live rescue session"); }
        GuardedFilesystem.RequireRoot();
        if (disk.BlockSize != 512 || disk.BlockCount < MinimumBytes / 512)
        {
            throw new IOException("installation requires a disk of at least 256 MiB with 512-byte sectors");
        }

        if (ReferenceEquals(disk, SystemMounts.RootPartition?.Host))
        {
            throw new IOException("refusing to install over the running system's disk");
        }

        foreach (var mount in VfsManager.Mounts)
        {
            if (mount.Partition is Partition mounted && ReferenceEquals(mounted.Host, disk))
            {
                throw new IOException("refusing to install over a disk with mounted partitions");
            }
        }

        // Validate source payloads before any destructive operation. No host filesystem
        // dependency: Limine supplies the original ELF and the bundled EFI module.
        NativeBootAssets assets = NativeBootAssets.Open();
        ulong espSectors = EspBytes / disk.BlockSize;
        ulong rootStart = Alignment + espSectors;
        ulong rootSectors = Math.Min((disk.BlockCount - 34 - rootStart) / Alignment * Alignment, Ext2Formatter.MaximumBytes / 512);   // keep clear of the backup GPT

        progress("Writing partition table (GPT)");
        Gpt.Create(disk);
        if (!Gpt.AddPartition(disk, Alignment, espSectors, EfiSystemPartitionType)
            || !Gpt.AddPartition(disk, rootStart, rootSectors, LinuxFilesystemType))
        {
            throw new IOException("could not create partitions");
        }

        GptChecksums.Complete(disk);

        StorageManager.RescanPartitions(disk);
        IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(disk);
        if (partitions.Count != 2)
        {
            throw new IOException("expected 2 partitions after rescan, found " + partitions.Count);
        }

        Partition esp = partitions[0], root = partitions[1];
        var identities = GptIdentity.Read(disk);
        Guid espId = identities[0].Id, rootId = identities[1].Id;
        string inventory = BootLayout.Inventory(rootId, espId, assets.KernelId, assets.EfiId, assets.Kernel.Length, assets.Efi.Length);

        progress("Formatting EFI system partition (FAT32, 128 MiB)");
        Format(esp, "EFI        ");
        progress("Formatting root partition (ext2, " + (rootSectors * disk.BlockSize / (1024 * 1024)) + " MiB)");
        Ext2Formatter.Format(root, "ZENITH");

        Directory.CreateDirectory(TargetMount);
        Directory.CreateDirectory(EspMount);
        Mount(root, TargetMount, SystemMounts.Ext2);
        try
        {
            progress("Creating directory layout");
            FileSystemLayout.Create(TargetMount);

            foreach (string tree in s_copiedTrees)
            {
                if (Directory.Exists(tree))
                {
                    progress("Copying " + tree);
                    Shell.Commands.FileCommands.CopyDirectory(tree, TargetMount + tree);
                }
            }

            if (profile is not null)
            {
                progress("Configuring hostname and initial accounts");
                ConfigureProfile(profile);
            }

            Directory.CreateDirectory(TargetMount + "/var/lib/zenith/boot");
            File.WriteAllText(TargetMount + "/var/lib/zenith/boot/installed.tsv", inventory);
            File.WriteAllText(TargetMount + "/etc/fstab",
                "# <device>  <mount point>  <type>  <options>\n" +
                "PARTUUID=" + rootId.ToString("D") + " / ext2 defaults\n" +
                "PARTUUID=" + espId.ToString("D") + " /boot/efi fat defaults\n" +
                "tmpfs /tmp tmpfs defaults\n");
            SystemMounts.SyncAll();
        }
        catch
        {
            VfsManager.TryUnmount(TargetMount);
            throw;
        }
        if (!VfsManager.TryUnmount(TargetMount)) { throw new IOException("cannot unmount installed root"); }

        Mount(esp, EspMount);
        try
        {
            progress("Copying UEFI bootloader and versioned kernel");
            Directory.CreateDirectory(EspMount + "/EFI/BOOT");
            string kernelPath = BootLayout.KernelPath(assets.KernelId);
            Directory.CreateDirectory(EspMount + "/boot/zenith/" + assets.KernelId);
            CopyBootFile(EspMount + "/EFI/BOOT/BOOTX64.EFI", assets.Efi);
            CopyBootFile(EspMount + kernelPath, assets.Kernel, progress: progress);
            File.WriteAllText(EspMount + "/boot/zenith/manifest.tsv", inventory);
            File.WriteAllText(EspMount + "/boot/zenith/" + assets.KernelId + "/manifest.tsv", inventory);
            SystemMounts.SyncAll(); disk.Flush();
            progress("Activating installed boot menu");
            // Boot configuration is written only after verified artifacts are flushed.
            File.WriteAllText(EspMount + "/limine.conf", BootLayout.Configuration(rootId, assets.KernelId));
            SystemMounts.SyncAll();
        }
        catch
        {
            VfsManager.TryUnmount(EspMount);
            throw;
        }
        if (!VfsManager.TryUnmount(EspMount)) { throw new IOException("cannot unmount installed EFI partition"); }

        disk.Flush();
        progress("Done");
    }

    private static void ConfigureProfile(InstallationProfile profile)
    {
        File.WriteAllText(TargetMount + "/etc/hostname", profile.Hostname + "\n");
        File.WriteAllText(TargetMount + "/etc/hosts", "127.0.0.1 localhost " + profile.Hostname + "\n");
        File.WriteAllText(TargetMount + "/etc/passwd", profile.Passwd);
        File.WriteAllText(TargetMount + "/etc/group", profile.Group);
        // Create shadow as private from the first write, rather than chmod after exposure.
        string shadow = TargetMount + "/etc/shadow";
        if (File.Exists(shadow)) { File.Delete(shadow); }
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(profile.Shadow);
        CopyBootFile(shadow, bytes, (VfsMode)0x180);
        bytes.AsSpan().Clear();
        var metadata = new VfsFileMetadata();
        if (!metadata.SetOwner(shadow, 0, 0) || !metadata.SetMode(shadow, 0x180)) { throw new IOException("cannot protect initial passwords"); }
        string home = TargetMount + "/home/" + profile.User;
        if (profile.User != "user")
        {
            if (Directory.Exists(home)) { throw new IOException("selected user home already exists in live files"); }
            if (!VfsManager.TryRename(TargetMount + "/home/user", home)) { throw new IOException("cannot prepare initial home"); }
        }
        SetHomeTree(home, metadata);
        foreach (string path in new[] { TargetMount + "/root", TargetMount + "/etc", TargetMount + "/home" })
        {
            if (!metadata.SetOwner(path, 0, 0) || !metadata.SetMode(path, path.EndsWith("/root") ? (ushort)0x1c0 : (ushort)0x1ed))
            { throw new IOException("cannot protect installed directories"); }
        }
    }
    private static void SetHomeTree(string path, VfsFileMetadata metadata)
    {
        if (!metadata.SetOwner(path, 1000, 1000) || !metadata.SetMode(path, Directory.Exists(path) ? (ushort)0x1c0 : (ushort)0x180))
        { throw new IOException("cannot secure initial home"); }
        foreach (string file in Directory.GetFiles(path)) { if (!metadata.SetOwner(file, 1000, 1000)) { throw new IOException("cannot own home file"); } }
        foreach (string directory in Directory.GetDirectories(path)) { SetHomeTree(directory, metadata); }
    }

    private static void CopyBootFile(string path, ReadOnlySpan<byte> bytes, VfsMode mode = (VfsMode)0x1a4, Action<string>? progress = null)
    {
        if (!VfsManager.TryCreateFile(path, mode) || !VfsManager.TryOpenFile(path, out var file))
        { throw new IOException("cannot create installed boot file"); }
        try
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                int count = Math.Min(65536, bytes.Length - offset);
                if (file.Write(bytes.Slice(offset, count)) != count) { throw new IOException("short boot file write"); }
                offset += count;
                if (offset % (1024 * 1024) == 0 || offset == bytes.Length)
                { progress?.Invoke("Copying kernel: " + ((long)offset * 100 / bytes.Length) + "%"); }
            }
            if (!file.TryFlush()) { throw new IOException("cannot flush boot file"); }
            file.Dispose();
        }
        catch { file.Dispose(); throw; }
        using var stream = File.OpenRead(path);
        try
        {
            byte[] buffer = new byte[65536]; int offset = 0;
            while (offset < bytes.Length)
            {
                int n = stream.Read(buffer, 0, Math.Min(buffer.Length, bytes.Length - offset));
                if (n == 0 || !bytes.Slice(offset, n).SequenceEqual(buffer.AsSpan(0, n))) { throw new IOException("installed boot file verification failed"); }
                offset += n;
            }
            if (stream.ReadByte() != -1) { throw new IOException("installed boot file length mismatch"); }
        }
        catch { stream.Dispose(); throw; }
    }

    private static void Format(Partition partition, string label)
    {
        var options = new FatFormatOptions { Type = FatType.Fat32, VolumeLabel = label };
        if (!VfsManager.TryFormat(SystemMounts.Fat, partition, options))
        {
            throw new IOException("could not format " + partition.Name);
        }
    }

    private static void Mount(Partition partition, string mountPoint, string filesystem = SystemMounts.Fat)
    {
        if (!VfsManager.TryMount(filesystem, partition, MountFlags.None, mountPoint, out _))
        {
            throw new IOException("could not mount " + partition.Name + " at " + mountPoint);
        }
    }
}
