using System;
using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Storage;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Explicit secondary-volume ext2 mount; formatting and destruction are not exposed.</summary>
internal sealed class Ext2FilesystemType : IVfsFilesystemType
{
    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = null;
        if (flags != MountFlags.None || !int.TryParse(source, out int index)
            || index < 0 || index >= StorageManager.Partitions.Count)
        {
            return false;
        }
        var device = StorageManager.Partitions[index];
        if (Ext2VolumePolicy.Check(device) is not null || !Ext2Superblock.TryCreate(device, out var mounted))
        {
            return false;
        }
        superblock = mounted;
        return true;
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;
}
