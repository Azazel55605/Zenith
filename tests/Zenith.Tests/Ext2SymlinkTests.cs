using System;
using System.Diagnostics;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class Ext2SymlinkTests
{
    [Theory]
    [InlineData(1)] [InlineData(59)] [InlineData(60)] [InlineData(61)] [InlineData(1023)]
    public void TargetStorageRemountAndDeletionAreIndependentlyReadable(int length)
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "LINKS");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        uint blocks = sb!.FreeBlocksCount, inodes = sb.FreeInodesCount;
        string target = new('x', length);
        Assert.True(sb.InodeOps.Symlink(sb.Root, "link", target, out var link));
        var node = Assert.IsType<Ext2Inode>(link);
        Assert.Equal(length < 60 ? 0u : 2u, node.Blocks);
        Assert.Equal(0xa1ff, node.Mode); // symlinks are 0777
        Assert.Equal(blocks - (length < 60 ? 0u : 1u), sb.FreeBlocksCount);
        disk.Flush();
        CheckFsck(disk.Path);
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        Assert.True(remounted!.InodeOps.Lookup(remounted.Root, "link", out var reopened));
        Assert.True(remounted.InodeOps.TryReadLink(reopened!, out string? actual));
        Assert.Equal(target, actual);
        Assert.True(remounted.InodeOps.Unlink(remounted.Root, "link"));
        Assert.Equal(blocks, remounted.FreeBlocksCount);
        Assert.Equal(inodes, remounted.FreeInodesCount);
        disk.Flush();
        CheckFsck(disk.Path);
    }

    [Fact]
    public void MultibyteTargetUsesByteBoundaryAndRemainsVerbatim()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        string target = new('é', 30); // 30 characters, 60 UTF-8 bytes
        Assert.True(sb.InodeOps.Symlink(sb.Root, "link", target, out var link));
        Assert.Equal(2u, Assert.IsType<Ext2Inode>(link).Blocks);
        Assert.True(sb.InodeOps.TryReadLink(link!, out string? actual));
        Assert.Equal(target, actual);
    }

    [Theory]
    [InlineData("")] [InlineData("bad\0target")]
    public void InvalidTargetsDoNotMutateDisk(string target)
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        byte[] before = (byte[])disk.Data.Clone();
        Assert.False(sb.InodeOps.Symlink(sb.Root, "link", target, out _));
        Assert.Equal(before, disk.Data);
    }

    [Theory]
    [InlineData(1024)] [InlineData(12289)] [InlineData(16384)]
    public void OversizedTargetRejectedBeforeAllocation(int length)
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        byte[] before = (byte[])disk.Data.Clone();
        Assert.False(sb.InodeOps.Symlink(sb.Root, "link", new string('x', length), out _));
        Assert.Equal(before, disk.Data);
    }

    [Theory]
    [InlineData("")] [InlineData(".")] [InlineData("..")] [InlineData("a/b")] [InlineData("a\0b")]
    public void InvalidNamesRejectedBeforeAllocation(string name)
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        byte[] before = (byte[])disk.Data.Clone();
        Assert.False(sb.InodeOps.Symlink(sb.Root, name, "target", out _));
        Assert.False(sb.InodeOps.Create(sb.Root, name, VfsMode.OwnerRead, out _));
        Assert.False(sb.InodeOps.Mkdir(sb.Root, name, VfsMode.OwnerRead, out _));
        Assert.False(sb.InodeOps.Unlink(sb.Root, name));
        Assert.False(sb.InodeOps.Rmdir(sb.Root, name));
        Assert.False(sb.InodeOps.Rename(sb.Root, name, sb.Root, "valid"));
        Assert.False(sb.InodeOps.Rename(sb.Root, "valid", sb.Root, name));
        Assert.Equal(before, disk.Data);
    }

    [Fact]
    public void Utf8NameLengthIsValidatedBeforeAllocation()
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        byte[] before = (byte[])disk.Data.Clone();
        Assert.False(sb.InodeOps.Symlink(sb.Root, new string('é', 128), "target", out _));
        Assert.Equal(before, disk.Data);
    }

    [Fact]
    public void ShortBlockBackedImportedLinkReadsDataNotPointerBytes()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        Assert.True(sb.TryAllocateBlock(0, out uint block));
        byte[] bytes = new byte[1024]; "path"u8.CopyTo(bytes);
        sb.WriteBlocks(block, 1, bytes);
        var link = new Ext2Inode(sb, 12, "link") { Mode = 0xa1ff, Size = 4, Blocks = 2 };
        link.Block[0] = block;
        Assert.True(sb.InodeOps.TryReadLink(link, out string? target));
        Assert.Equal("path", target);
    }

    [Theory]
    [InlineData(61, 0)] [InlineData(1024, 2)] [InlineData(uint.MaxValue, 2)] [InlineData(0, 0)]
    public void CorruptSizeOrStorageIsRejectedWithoutUnboundedAllocation(uint size, uint blocks)
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        var link = new Ext2Inode(sb, 12, "link") { Mode = 0xa1ff, Size = size, Blocks = blocks };
        Assert.False(sb.InodeOps.TryReadLink(link, out _));
    }

    [Fact]
    public void DiskFullSlowLinkReleasesItsInode()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        while (sb.TryAllocateBlock(0, out _)) { }
        uint before = sb.FreeInodesCount;
        Assert.False(sb.InodeOps.Symlink(sb.Root, "link", new string('x', 60), out _));
        Assert.Equal(before, sb.FreeInodesCount);
    }

    private static void CheckFsck(string path)
    {
        var info = new ProcessStartInfo("e2fsck") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-f"); info.ArgumentList.Add("-n"); info.ArgumentList.Add(path);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "e2fsck timed out");
        Assert.True(process.ExitCode == 0, output);
    }
}
