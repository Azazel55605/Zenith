using System;
using System.Runtime.CompilerServices;
using Cosmos.Kernel.HAL.Vfs;

namespace Zenith.Core.Security;

public static class FileAccessPolicy
{
    public static void Require(IVfsInode inode, int access)
    {
        if (!inode.InodeOperations.GetAttr(inode, out var stat)
            || !PermissionPolicy.Allows(CredentialContext.Current, stat.Uid, stat.Gid, (ushort)stat.Mode, access))
        { throw new UnauthorizedAccessException("permission denied"); }
    }
    public static void RequireRemoval(IVfsInode parent, ReadOnlySpan<char> name)
    {
        Require(parent, PermissionPolicy.Write | PermissionPolicy.Search);
        if (!parent.InodeOperations.GetAttr(parent, out var stat)
            || !parent.InodeOperations.Lookup(parent, name, out var child)
            || !child.InodeOperations.GetAttr(child, out var childStat)
            || !PermissionPolicy.CanRemove(CredentialContext.Current, stat.Uid, stat.Gid, (ushort)stat.Mode, childStat.Uid))
        { throw new UnauthorizedAccessException("permission denied"); }
    }
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "MarkPendingIfOpen")]
    public static extern bool MarkPending([UnsafeAccessorType("Cosmos.Kernel.System.Vfs.VfsManager, Cosmos.Kernel.System")] object? manager, string path);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "MakeAbsolute")]
    public static extern string MakeAbsolute([UnsafeAccessorType("Cosmos.Kernel.System.Vfs.VfsManager, Cosmos.Kernel.System")] object? manager, string path);

    // Invoked at the beginning of the released PAL's Open method by the local
    // patcher fixup, before it can allocate a tracked handle or truncate a file.
    public static void PrepareOpen(string path, int flags)
    {
        path = MakeAbsolute(null, path);
        if (!TryStat(null, path, out var stat)) { return; }
        int access = (flags & 15) switch
        {
            0 => PermissionPolicy.Read,
            1 => PermissionPolicy.Write,
            2 => PermissionPolicy.Read | PermissionPolicy.Write,
            _ => 0
        };
        if (!PermissionPolicy.Allows(CredentialContext.Current, stat.Uid, stat.Gid, (ushort)stat.Mode, access))
        { throw new UnauthorizedAccessException("permission denied"); }
    }
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "TryStat")]
    private static extern bool TryStat([UnsafeAccessorType("Cosmos.Kernel.System.Vfs.VfsManager, Cosmos.Kernel.System")] object? manager, string path, out VfsStat stat);
}
