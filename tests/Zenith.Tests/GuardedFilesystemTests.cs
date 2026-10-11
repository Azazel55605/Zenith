using System;
using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Security;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class GuardedFilesystemTests
{
    private sealed class Driver : IVfsFilesystemType
    {
        public IVfsSuperblock Volume = null!;
        public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? sb) { sb = Volume; return true; }
        public bool TryFormat(ReadOnlySpan<char> s, IVfsFormatOptions? o) => true;
        public bool TryDestroy(ReadOnlySpan<char> s) => true;
    }
    private sealed class Open : IVfsOpenFile
    {
        public Open(IVfsInode inode) => Inode = inode;
        public IVfsInode Inode { get; }
        public IFileOperations Operations => Inode.FileOperations!;
        public long Position { get; set; }
    }

    [Fact]
    public void DriverBoundaryEnforcesReadWriteTraversalAndOwnershipAcrossHandles()
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "ACCESS"); Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        var driver = new GuardedFilesystem(new Driver { Volume = sb! });
        using var root = CredentialContext.Enter(Credentials.Root);
        Assert.True(driver.TryMount("", MountFlags.None, out var mount));
        var parent = mount!.Root; var ops = parent.InodeOperations;
        Assert.True(ops.Mkdir(parent, "home", (VfsMode)0x1c0, out var home));
        Assert.True(ops.SetAttr(home!, SetAttrFlags.Uid | SetAttrFlags.Gid, new VfsStat { Uid = 1000, Gid = 100 }));
        IVfsInode file;
        using (CredentialContext.Enter(new Credentials(1000, 100)))
        {
            Assert.True(ops.Lookup(parent, "home", out var reopened));
            Assert.True(reopened!.InodeOperations.Create(reopened, "private", (VfsMode)0x180, out var made)); file = made!;
            Assert.True(file.InodeOperations.GetAttr(file, out var stat)); Assert.Equal(1000u, stat.Uid); Assert.Equal(100u, stat.Gid);
            var open = new Open(file); Assert.Equal(3, open.Operations.Write(open, new byte[] {1,2,3}));
            Assert.True(file.InodeOperations.SetAttr(file, SetAttrFlags.Mode, new VfsStat { Mode = 0 }));
            Assert.Throws<UnauthorizedAccessException>(() => open.Operations.Read(open, new byte[3]));
            Assert.Throws<UnauthorizedAccessException>(() => file.InodeOperations.SetAttr(file, SetAttrFlags.Uid, new VfsStat { Uid = 0 }));
        }
        using (CredentialContext.Enter(new Credentials(2000, 200)))
        {
            Assert.Throws<UnauthorizedAccessException>(() => home!.InodeOperations.Lookup(home, "private", out _));
            Assert.Throws<UnauthorizedAccessException>(() => home!.InodeOperations.ReadDir(home, out _));
            Assert.Throws<UnauthorizedAccessException>(() => file.InodeOperations.SetAttr(file, SetAttrFlags.Mode, new VfsStat { Mode = (VfsMode)0x1ff }));
            Assert.Throws<UnauthorizedAccessException>(() => ops.Unlink(home!, "private"));
            Assert.Throws<UnauthorizedAccessException>(() => driver.TryDestroy(""));
        }
        // The held proxy observes mode changes from a raw independently opened inode.
        var inner = GuardedFilesystem.Unwrap(file);
        Assert.True(inner.InodeOperations.SetAttr(inner, SetAttrFlags.Mode, new VfsStat { Mode = (VfsMode)0x1a4 }));
        using (CredentialContext.Enter(new Credentials(2000, 200)))
        {
            var open = new Open(file); Assert.Equal(3, open.Operations.Read(open, new byte[3]));
            Assert.Throws<UnauthorizedAccessException>(() => open.Operations.Write(open, new byte[1]));
        }
        Assert.True(ops.Unlink(home!, "private")); Assert.True(ops.Rmdir(parent, "home"));
    }

    [Fact]
    public void StickyDirectoriesPreventForeignRemovalAndCreationInheritsSetgid()
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "STICKY"); Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        using var root = CredentialContext.Enter(Credentials.Root);
        var driver = new GuardedFilesystem(new Driver { Volume = sb! }); Assert.True(driver.TryMount("", MountFlags.None, out var mount));
        var dir = mount!.Root;
        Assert.True(dir.InodeOperations.SetAttr(dir, SetAttrFlags.Mode | SetAttrFlags.Gid, new VfsStat { Mode = (VfsMode)0x7ff, Gid = 88 }));
        using (CredentialContext.Enter(new Credentials(1000, 100)))
        {
            Assert.True(dir.InodeOperations.Mkdir(dir, "folder", (VfsMode)0x1ff, out var child));
            Assert.True(child!.InodeOperations.GetAttr(child, out var stat)); Assert.Equal(88u, stat.Gid); Assert.Equal(0x45ed, (int)stat.Mode);
        }
        using (CredentialContext.Enter(new Credentials(2000, 200)))
        {
            Assert.Throws<UnauthorizedAccessException>(() => dir.InodeOperations.Rmdir(dir, "folder"));
            Assert.Throws<UnauthorizedAccessException>(() => dir.InodeOperations.Rename(dir, "folder", dir, "stolen"));
        }
        using (CredentialContext.Enter(new Credentials(1000, 100))) { Assert.True(dir.InodeOperations.Rmdir(dir, "folder")); }
    }
}
