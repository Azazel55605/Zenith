using System;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Shell;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Core.Storage;

/// <summary>Bridge the plain shell metadata contract to the installed Cosmos VFS.</summary>
internal sealed class VfsFileMetadata : IFileMetadata
{
    public bool TryRead(string path, out FileMetadata metadata)
    {
        metadata = default;
        IVfsInode? inode = null;
        bool mountRoot = path == "/";
        foreach (var mount in VfsManager.Mounts) { mountRoot |= mount.MountPoint == path; }
        if (mountRoot)
        {
            if (!VfsManager.TryOpenDirectory(path, out var root)) { return false; }
            using (root) { inode = root.Inode; }
        }
        else
        {
            if (!VfsManager.TryOpenDirectory(Path.GetDirectoryName(path) ?? "/", out var parent)) { return false; }
            using (parent)
            {
                if (!parent.Inode.InodeOperations.Lookup(parent.Inode, Path.GetFileName(path), out inode)) { return false; }
            }
        }
        if (!inode.InodeOperations.GetAttr(inode, out VfsStat stat)) { return false; }
        string? target = null;
        if (stat.IsSymbolicLink) { inode.InodeOperations.TryReadLink(inode, out target); }
        // FAT's synthesized Unix bits are not persisted ownership or permissions.
        bool unix = inode is Ext2Inode || Proc.ProcFilesystemType.Owns(inode) || Dev.DevFilesystemType.Owns(inode);
        metadata = new((ushort)stat.Mode, stat.Uid, stat.Gid, stat.NLink, stat.Size,
            stat.Mtime.TvSec, stat.Ino, stat.Blocks, stat.BlkSize, unix, target);
        return true;
    }

    public bool SetMode(string path, ushort mode)
        => Set(path, SetAttrFlags.Mode, new VfsStat { Mode = (VfsMode)mode });

    public bool SetOwner(string path, uint uid, uint? gid)
        => Set(path, SetAttrFlags.Uid | (gid.HasValue ? SetAttrFlags.Gid : SetAttrFlags.None),
            new VfsStat { Uid = uid, Gid = gid ?? 0 });

    private static bool Set(string path, SetAttrFlags flags, VfsStat attributes)
    {
        if (VfsManager.TryOpenDirectory(path, out var directory))
        {
            using (directory) { return Apply(directory.Inode, flags, attributes); }
        }
        if (VfsManager.TryOpenFile(path, out var file))
        {
            using (file) { return Apply(file.Inode, flags, attributes); }
        }
        return false;
    }

    private static bool Apply(IVfsInode inode, SetAttrFlags flags, VfsStat attributes)
        => inode is Ext2Inode && inode.InodeOperations.SetAttr(inode, flags, attributes);
}
