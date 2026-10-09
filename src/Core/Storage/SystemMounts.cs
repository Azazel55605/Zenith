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

    public static void Initialize()
    {
        VfsManager.RegisterFilesystem(Fat, new FatFilesystemType());
        VfsManager.RegisterFilesystem(Ext2, new Ext2FilesystemType());

        RootPartition = FindInstalledRoot();
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
        Log.PersistTo("/var/log/boot.log");
        MountRamDisk("tmpfs", 16, "/tmp");
        ProcMount.Initialize();
        DevMount.Initialize();
    }

    /// <summary>Flushes every mounted filesystem to its disk (the <c>sync</c> command). Returns how many were synced.</summary>
    public static int SyncAll()
    {
        int synced = 0;
        foreach (VfsManager.VfsMount mount in VfsManager.Mounts)
        {
            if (mount.Superblock.SuperOperations.Sync(mount.Superblock))
            {
                synced++;
            }
        }

        return synced;
    }

    /// <summary>
    /// Mounts a FAT or explicitly selected ext2 partition (by name, e.g. <c>sata0p1</c>) on an existing directory.
    /// Returns an error message, or null on success.
    /// </summary>
    public static string? Mount(string partitionName, string mountPoint, string filesystem = Fat)
    {
        lock (s_mountGate)
        {
            return MountCore(partitionName, mountPoint, filesystem);
        }
    }

    private static string? MountCore(string partitionName, string mountPoint, string filesystem)
    {
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

    /// <summary>Formats an explicitly selected, unmounted secondary partition.</summary>
    public static string? FormatExt2(string name, string label, bool confirmed)
    {
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
        lock (s_mountGate)
        {
            return UnmountCore(mountPoint);
        }
    }

    private static string? UnmountCore(string mountPoint)
    {
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
        Log.StopPersisting();
        SyncAll();

        var mounts = new List<VfsManager.VfsMount>(VfsManager.Mounts);
        mounts.Sort((a, b) => b.MountPoint.Length.CompareTo(a.MountPoint.Length));
        foreach (VfsManager.VfsMount mount in mounts)
        {
            VfsManager.TryUnmount(mount.MountPoint);
        }

        foreach (IBlockDevice device in StorageManager.Devices)
        {
            device.Flush();
        }

        Log.Write("mounts", "all filesystems unmounted");
    }

    /// <summary>Mounts every partition in turn at <c>/</c> and keeps the first one carrying a Zenith installation.</summary>
    private static Partition? FindInstalledRoot()
    {
        foreach (Partition partition in StorageManager.Partitions)
        {
            if (!VfsManager.TryMount(Fat, partition, MountFlags.None, "/", out _))
            {
                continue;   // not FAT, or unreadable
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

    private static void MountRamDisk(string name, ulong megabytes, string mountPoint)
    {
        var device = new MemoryBlockDevice(name, SectorSize, megabytes * 1024 * 1024 / SectorSize);
        if (!VfsManager.RegisterFilesystem(name, new FatFilesystemType(device))
            || !VfsManager.TryFormat(name, "", new FatFormatOptions { Type = FatType.Fat16 })
            || !VfsManager.TryMount(name, "", MountFlags.None, mountPoint, out _))
        {
            Log.Write("mounts", "failed to mount RAM disk " + name + " at " + mountPoint);
        }
    }
}
