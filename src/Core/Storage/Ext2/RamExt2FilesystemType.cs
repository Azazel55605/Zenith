using System;
using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;

namespace Zenith.Core.Storage.Ext2;

internal sealed class RamExt2FilesystemType : IVfsFilesystemType
{
    private readonly IBlockDevice _device;
    public RamExt2FilesystemType(IBlockDevice device) => _device = device;
    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = null;
        if (flags != MountFlags.None || !Ext2Superblock.TryCreate(_device, out var volume)) { return false; }
        volume.MarkMounted(); superblock = volume; return true;
    }
    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) { Ext2Formatter.Format(_device, "tmp"); return true; }
    public bool TryDestroy(ReadOnlySpan<char> source) => false;
}
