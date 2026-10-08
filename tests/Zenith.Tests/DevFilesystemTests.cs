using System;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Dev;

namespace Zenith.Tests;

public class DevFilesystemTests
{
    private sealed class OpenFile(IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get; set; }
    }

    private static IVfsSuperblock Mount(MountFlags flags = MountFlags.None)
    {
        Assert.True(new DevFilesystemType().TryMount("", flags, out var sb));
        return sb;
    }

    private static OpenFile Open(IVfsSuperblock sb, string name)
    {
        Assert.True(sb.Root.InodeOperations.Lookup(sb.Root, name, out var node));
        return new OpenFile(node);
    }

    [Theory]
    [InlineData("null", 0x103ul)]
    [InlineData("zero", 0x105ul)]
    public void DevicesHaveCharacterMetadataAndDiscardEveryWrite(string name, ulong rdev)
    {
        var file = Open(Mount(), name);
        Assert.True(file.Inode.InodeOperations.GetAttr(file.Inode, out var stat));
        Assert.Equal(VfsMode.CharacterDevice, stat.Mode & VfsMode.FileTypeMask);
        Assert.Equal((VfsMode)0x1B6, stat.Mode & VfsMode.PermissionMask);
        Assert.Equal(rdev, stat.Rdev);
        Assert.Equal(0ul, stat.Size);
        Assert.Equal(0, file.Operations.Write(file, ReadOnlySpan<byte>.Empty));
        Assert.Equal(3, file.Operations.Write(file, new byte[] { 7, 8, 9 }));
        Assert.Equal(0, file.Position); // The VFS owns position advancement.
        Assert.True(file.Operations.Fsync(file));
    }

    [Fact]
    public void NullReadIsEofAndDoesNotTouchBuffer()
    {
        var file = Open(Mount(), "null");
        byte[] data = { 1, 2, 3 };
        Assert.Equal(0, file.Operations.Read(file, data));
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
        file.Position = 900;
        Assert.Equal(0, file.Operations.Read(file, data));
    }

    [Fact]
    public void ZeroReadFillsOnlyRequestedSpanAndNeverReachesEof()
    {
        var file = Open(Mount(), "zero");
        byte[] data = { 7, 7, 7, 7, 7 };
        Assert.Equal(3, file.Operations.Read(file, data.AsSpan(1, 3)));
        Assert.Equal(new byte[] { 7, 0, 0, 0, 7 }, data);
        file.Position = long.MaxValue - 5;
        Assert.Equal(5, file.Operations.Read(file, data));
        Assert.Equal(new byte[5], data);
        Assert.Equal(0, file.Operations.Read(file, Span<byte>.Empty));
        var other = Open(Mount(), "zero");
        Assert.Equal(5, other.Operations.Read(other, data));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("zero")]
    public void SeekAndZeroTruncationAreNoOpsAndNonzeroResizingIsRejected(string name)
    {
        var file = Open(Mount(), name);
        foreach (SeekWhence whence in new[] { SeekWhence.Set, SeekWhence.Cur, SeekWhence.End })
        {
            file.Position = 5;
            Assert.True(file.Operations.Seek(file, -100, whence, out var position));
            Assert.Equal(0, position);
            Assert.Equal(0, file.Position);
        }
        Assert.False(file.Operations.Seek(file, 0, (SeekWhence)99, out _));
        var ops = file.Inode.InodeOperations;
        Assert.True(ops.SetAttr(file.Inode, SetAttrFlags.Size, new VfsStat()));
        Assert.False(ops.SetAttr(file.Inode, SetAttrFlags.Size, new VfsStat { Size = 123 }));
        Assert.True(ops.GetAttr(file.Inode, out var stat));
        Assert.Equal(0ul, stat.Size);
        Assert.False(ops.SetAttr(file.Inode, SetAttrFlags.Mode, stat));
    }

    [Fact]
    public void NamespaceIsFixedCaseSensitiveAndNotFormattable()
    {
        var sb = Mount();
        var root = sb.Root;
        var ops = root.InodeOperations;
        Assert.True(ops.ReadDir(root, out var nodes));
        Assert.Equal(2, nodes.Count);
        Assert.True(ops.GetAttr(root, out var stat));
        Assert.True(stat.IsDirectory);
        Assert.Null(root.FileOperations);
        Assert.NotEqual(nodes[0].Name, nodes[1].Name);
        Assert.False(ops.Lookup(root, "Null", out _));
        Assert.False(ops.Lookup(root, "random", out _));
        Assert.False(ops.ReadDir(nodes[0], out var empty));
        Assert.Empty(empty);
        Assert.False(ops.Create(root, "file", 0, out _));
        Assert.False(ops.Mkdir(root, "dir", 0, out _));
        Assert.False(ops.Symlink(root, "link", "null", out _));
        Assert.False(ops.Unlink(root, "null"));
        Assert.False(ops.Rmdir(root, "dir"));
        Assert.False(ops.Rename(root, "null", root, "gone"));
        Assert.False(ops.SetAttr(root, SetAttrFlags.Size, new VfsStat()));
        var driver = new DevFilesystemType();
        Assert.False(driver.TryMount("disk", MountFlags.None, out _));
        Assert.False(driver.TryFormat("", null));
        Assert.False(driver.TryDestroy(""));
        Assert.True(sb.SuperOperations.StatFs(sb, out var fs));
        Assert.Equal(3ul, fs.Files);
        Assert.Equal(0ul, fs.Blocks);
    }

    [Fact]
    public void ReadOnlyMountStillReadsButRejectsWritesAndTruncation()
    {
        var file = Open(Mount(MountFlags.ReadOnly), "zero");
        Assert.Equal(2, file.Operations.Read(file, new byte[2]));
        Assert.Throws<IOException>(() => file.Operations.Write(file, new byte[1]));
        Assert.False(file.Inode.InodeOperations.SetAttr(file.Inode, SetAttrFlags.Size, new VfsStat()));
        Assert.True(file.Inode.InodeOperations.GetAttr(file.Inode, out var stat));
        Assert.Equal((VfsMode)0x124, stat.Mode & VfsMode.PermissionMask);
    }
}
