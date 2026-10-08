using System;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;

namespace Zenith.Core.Storage.Dev;

internal static class DevMount
{
    public static void Initialize()
    {
        if (!VfsManager.RegisterFilesystem("dev", new DevFilesystemType())
            || !VfsManager.TryMount("dev", "", MountFlags.NoExec | MountFlags.NoSuid, "/dev", out _))
        {
            throw new InvalidOperationException("Failed to mount /dev");
        }
        Log.Write("mounts", "dev mounted at /dev (null, zero)");
    }
}
