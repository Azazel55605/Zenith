using System;
using System.Collections.Generic;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;

namespace Zenith.Core.Storage.Dev;

internal static class DevMount
{
    private static IReadOnlyList<IBlockDevice> Devices()
    {
        // Capture both replaced-whole tables once. Ignore orphan partitions during
        // a hot-unplug race; held nodes also recheck presence on every I/O call.
        IReadOnlyList<IBlockDevice> disks = StorageManager.Devices;
        IReadOnlyList<Partition> partitions = StorageManager.Partitions;
        var devices = new List<IBlockDevice>(disks);
        foreach (Partition partition in partitions)
        {
            foreach (IBlockDevice disk in disks)
            {
                if (ReferenceEquals(partition.Host, disk))
                {
                    devices.Add(partition);
                    break;
                }
            }
        }
        return devices;
    }

    public static void Initialize()
    {
        if (!VfsManager.RegisterFilesystem("dev", new DevFilesystemType(Devices))
            || !VfsManager.TryMount("dev", "", MountFlags.NoExec | MountFlags.NoSuid, "/dev", out _))
        {
            throw new InvalidOperationException("Failed to mount /dev");
        }
        Log.Write("mounts", "dev mounted at /dev (null, zero, read-only disks)");
    }
}
