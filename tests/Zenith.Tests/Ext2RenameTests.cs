using System;
using System.Diagnostics;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public sealed class Ext2RenameTests
{
    [Fact]
    public void DirectoryMovePreservesIdentityAndUpdatesParentsAcrossRemount()
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "MOVE");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        var ops = sb!.InodeOps;
        Assert.True(ops.Mkdir(sb.Root, "left", (VfsMode)0x1ed, out var left));
        Assert.True(ops.Mkdir(sb.Root, "right", (VfsMode)0x1ed, out var right));
        Assert.True(ops.Mkdir(left!, "tree", (VfsMode)0x5e8, out var tree));
        Assert.True(ops.Create(tree!, "child", VfsMode.OwnerRead, out _));
        Assert.True(ops.SetAttr(tree!, SetAttrFlags.Mode | SetAttrFlags.Uid | SetAttrFlags.Gid, new VfsStat { Mode = (VfsMode)0x5e8, Uid = 70000, Gid = 80000 }));
        uint number = ((Ext2Inode)tree!).InodeNumber;
        Assert.True(ops.Rename(left!, "tree", right!, "moved"));
        Assert.False(ops.Lookup(left!, "tree", out _));
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        var root = remounted!.Root;
        Assert.True(remounted.InodeOps.Lookup(root, "left", out left));
        Assert.True(remounted.InodeOps.Lookup(root, "right", out right));
        Assert.Equal(2, ((Ext2Inode)left!).LinksCount);
        Assert.Equal(3, ((Ext2Inode)right!).LinksCount);
        Assert.True(remounted.InodeOps.Lookup(right!, "moved", out tree));
        Assert.Equal(number, ((Ext2Inode)tree!).InodeNumber);
        Assert.Equal(0x45e8, ((Ext2Inode)tree).Mode);
        Assert.Equal(70000u, ((Ext2Inode)tree).Uid);
        Assert.True(remounted.InodeOps.Lookup(tree, "..", out var parent));
        Assert.Equal(((Ext2Inode)right!).InodeNumber, ((Ext2Inode)parent!).InodeNumber);
        Assert.True(remounted.InodeOps.Lookup(tree, "child", out _));
        // Different handles for one parent must not undo directory expansion/links.
        Assert.True(remounted.InodeOps.Lookup(root, "right", out var alias));
        Assert.True(remounted.InodeOps.Rename(right!, "moved", alias!, "renamed"));
        Assert.True(remounted.InodeOps.Rename(alias!, "renamed", right!, "renamed"));
        disk.Flush();
        var info = new ProcessStartInfo("e2fsck") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-fn"); info.ArgumentList.Add(disk.Path);
        using var check = Process.Start(info)!;
        string output = check.StandardOutput.ReadToEnd() + check.StandardError.ReadToEnd();
        check.WaitForExit(); Assert.True(check.ExitCode == 0, output);
    }

    [Fact]
    public void ParentRecordDoesNotNeedToFollowMinimumSizedDotRecord()
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "MOVE");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        var ops = sb!.InodeOps;
        Assert.True(ops.Mkdir(sb.Root, "tree", (VfsMode)0x1ed, out var tree));
        Assert.True(ops.Mkdir(sb.Root, "destination", (VfsMode)0x1ed, out var destination));
        var moved = (Ext2Inode)tree!;
        byte[] block = disk.Read(moved.Block[0]);
        // Legal padding in '.' changes the location of '..' from 12 to 20.
        block.AsSpan(12, 12).CopyTo(block.AsSpan(20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), 20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(24), 1004);
        sb.WriteBlocks(moved.Block[0], 1, block);
        Assert.True(ops.Rename(sb.Root, "tree", destination!, "moved"));
        Assert.True(ops.Lookup(moved, "..", out var parent));
        Assert.Equal(((Ext2Inode)destination!).InodeNumber, ((Ext2Inode)parent!).InodeNumber);
        disk.Flush();
        var info = new ProcessStartInfo("e2fsck") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-fn"); info.ArgumentList.Add(disk.Path);
        using var check = Process.Start(info)!;
        string output = check.StandardOutput.ReadToEnd() + check.StandardError.ReadToEnd();
        check.WaitForExit(); Assert.True(check.ExitCode == 0, output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptAncestryAndLinkOverflowAreRejectedBeforeWrites(bool corruptAncestry)
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "MOVE");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        var ops = sb!.InodeOps;
        Assert.True(ops.Mkdir(sb.Root, "tree", (VfsMode)0x1ed, out _));
        Assert.True(ops.Mkdir(sb.Root, "destination", (VfsMode)0x1ed, out var target));
        var destination = (Ext2Inode)target!;
        if (corruptAncestry)
        {
            Assert.True(Ext2DirectoryHelper.TryFind(sb, destination, "..", out var parent));
            byte[] block = disk.Read(destination.Block[0]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(parent.Offset), destination.InodeNumber);
            sb.WriteBlocks(destination.Block[0], 1, block);
        }
        else
        {
            destination.LinksCount = ushort.MaxValue;
            sb.WriteInode(destination);
        }
        int writes = disk.Writes;
        Assert.False(ops.Rename(sb.Root, "tree", destination, "moved"));
        Assert.Equal(writes, disk.Writes);
        Assert.True(ops.Lookup(sb.Root, "tree", out _));
    }

    [Fact]
    public void InvalidMovesDoNotWriteOrRemoveEntries()
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        using var foreign = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "MOVE"); Ext2Formatter.Format(foreign, "OTHER");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.True(Ext2Superblock.TryCreate(foreign, out var other));
        var ops = sb!.InodeOps;
        Assert.True(ops.Mkdir(sb.Root, "tree", (VfsMode)0x1ed, out var tree));
        Assert.True(ops.Mkdir(tree!, "sub", (VfsMode)0x1ed, out var sub));
        Assert.True(ops.Create(sb.Root, "file", VfsMode.OwnerRead, out var file));
        int writes = disk.Writes;
        Assert.False(ops.Rename(sb.Root, "tree", tree!, "self"));
        Assert.False(ops.Rename(sb.Root, "tree", sub!, "cycle"));
        Assert.False(ops.Rename(sb.Root, "tree", sb.Root, "file"));
        Assert.False(ops.Rename(sb.Root, "tree", file!, "bad"));
        Assert.False(ops.Rename(sb.Root, "tree", other!.Root, "foreign"));
        Assert.False(ops.Rename(sb.Root, "missing", sb.Root, "new"));
        Assert.Equal(writes, disk.Writes);
        Assert.True(ops.Lookup(sb.Root, "tree", out _));
    }
}
