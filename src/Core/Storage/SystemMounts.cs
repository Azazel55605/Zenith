using System;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Filesystems.Fat;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;

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

    private const ulong SectorSize = 512;

    public static BootMode Mode { get; private set; }

    /// <summary>The partition mounted at <c>/</c> when <see cref="Mode"/> is <see cref="BootMode.Installed"/>.</summary>
    public static Partition? RootPartition { get; private set; }

    public static void Initialize()
    {
        VfsManager.RegisterFilesystem(Fat, new FatFilesystemType());

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
        MountRamDisk("tmpfs", 16, "/tmp");
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
