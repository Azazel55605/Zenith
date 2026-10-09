using System;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class Ext2LifecycleTests
{
    private sealed class OpenFile(Ext2Inode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.Superblock.FileOps;
        public long Position { get; set; }
    }

    private static OpenFile File(Ext2Superblock sb) => new(new Ext2Inode(sb, 12, "file")
    {
        Mode = 0x81a4, LinksCount = 1,
    });

    private static void Resize(OpenFile file, ulong size)
    {
        var stat = new VfsStat { Size = size };
        Assert.True(file.Inode.InodeOperations.SetAttr(file.Inode, SetAttrFlags.Size, stat));
    }

    [Theory]
    [InlineData(2)] [InlineData(20)] [InlineData(269)]
    public void TruncateSparseFileReclaimsDataAndIndirectBlocks(int blocks)
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        var file = File(sb);
        uint initial = sb.FreeBlocksCount;
        file.Position = (blocks - 1L) * 1024;
        Assert.Equal(1, file.Operations.Write(file, new byte[] { 42 }));
        Assert.True(sb.FreeBlocksCount < initial);
        Resize(file, 0);
        Assert.Equal(initial, sb.FreeBlocksCount);
        Assert.Equal(0u, ((Ext2Inode)file.Inode).Blocks);
        Assert.All(((Ext2Inode)file.Inode).Block, value => Assert.Equal(0u, value));
    }

    [Fact]
    public void PartialShrinkThenGrowDoesNotExposeOldTailAcrossRemount()
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        var file = File(sb);
        byte[] original = new byte[1024]; Array.Fill(original, (byte)0x7f);
        Assert.Equal(1024, file.Operations.Write(file, original));
        Resize(file, 17); Resize(file, 800);
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        var reopened = new OpenFile(remounted!.ReadInode(12, "file"));
        byte[] data = new byte[1024];
        Assert.Equal(800, reopened.Operations.Read(reopened, data));
        Assert.All(data[..17], value => Assert.Equal(0x7f, value));
        Assert.All(data[17..800], value => Assert.Equal(0, value));
    }

    [Fact]
    public void GrowingImportedFileClearsBytesPastItsOldEof()
    {
        var (_, sb) = Ext2AllocationTests.Mount(); var file = File(sb);
        byte[] original = new byte[1024]; Array.Fill(original, (byte)0x7f);
        file.Operations.Write(file, original);
        ((Ext2Inode)file.Inode).Size = 17; // an imported inode with nonzero bytes beyond EOF
        Resize(file, 800);
        byte[] data = new byte[800]; file.Operations.Read(file, data);
        Assert.All(data[..17], value => Assert.Equal(0x7f, value));
        Assert.All(data[17..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void DoubleIndirectShrinkRetainsEarlierSparseBranch()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        var file = File(sb);
        uint initial = sb.FreeBlocksCount;
        file.Position = 268 * 1024; file.Operations.Write(file, new byte[] { 11 });
        file.Position = 524 * 1024; file.Operations.Write(file, new byte[] { 22 });
        Assert.Equal(initial - 5, sb.FreeBlocksCount); // top + two child tables + two data blocks
        Resize(file, 269 * 1024);
        Assert.Equal(initial - 3, sb.FreeBlocksCount);
        file.Position = 268 * 1024;
        byte[] data = new byte[1]; Assert.Equal(1, file.Operations.Read(file, data));
        Assert.Equal(11, data[0]);
        Resize(file, 0); Assert.Equal(initial, sb.FreeBlocksCount);
    }

    [Fact]
    public void InlineSymlinkDeletionDoesNotTreatTargetBytesAsBlockPointers()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        Assert.True(sb.TryAllocateBlock(0, out uint existing));
        var link = new Ext2Inode(sb, 12, "link") { Mode = 0xa1ff, Size = 4 };
        link.Block[0] = existing; // inline target bytes happen to encode a valid allocated block
        uint free = sb.FreeBlocksCount;
        sb.Truncate(link, 0);
        Assert.Equal(free, sb.FreeBlocksCount);
        Assert.Equal(0u, link.Blocks);
    }

    [Fact]
    public void DiskFullWriteReclaimsUnfinishedDoubleIndirectTables()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        while (sb.FreeBlocksCount > 2) Assert.True(sb.TryAllocateBlock(0, out _));
        var file = File(sb); file.Position = 268 * 1024;
        Assert.Equal(0, file.Operations.Write(file, new byte[] { 1 }));
        Assert.Equal(2u, sb.FreeBlocksCount);
        Assert.Equal(0u, ((Ext2Inode)file.Inode).Blocks);
        Assert.All(((Ext2Inode)file.Inode).Block, value => Assert.Equal(0u, value));
    }

    [Fact]
    public void ResizeAndWriteRejectUnsupportedSizesBeforeAllocation()
    {
        var (_, sb) = Ext2AllocationTests.Mount(); var file = File(sb);
        uint free = sb.FreeBlocksCount;
        var stat = new VfsStat { Size = sb.MaxFileSize + 1 };
        Assert.False(file.Inode.InodeOperations.SetAttr(file.Inode, SetAttrFlags.Size, stat));
        file.Position = (long)sb.MaxFileSize;
        Assert.Equal(0, file.Operations.Write(file, new byte[] { 1 }));
        Assert.Equal(free, sb.FreeBlocksCount);
    }

    [Fact]
    public void SymlinkSizeChangesAreRejectedWithoutChangingTarget()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        var link = new Ext2Inode(sb, 12, "link") { Mode = 0xa1ff, Size = 4 };
        link.Block[0] = 0x64636261;
        var stat = new VfsStat { Size = 0 };
        Assert.False(link.InodeOperations.SetAttr(link, SetAttrFlags.Size, stat));
        Assert.Equal("abcd", Ext2FileOperations.ReadSymlinkTarget(link));
    }

    [Fact]
    public void RemovingExpandedEmptyDirectoryReclaimsEveryBlock()
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        disk.Data[4096] = 0xff; disk.Data[4097] = 0x07; // reserved inode bits
        disk.Put(7 * 1024 + 4, 1024); // empty root-directory record spanning its block
        Assert.True(sb.InodeOps.Mkdir(sb.Root, "empty", (VfsMode)511, out var child));
        var dir = (Ext2Inode)child!;
        uint freeAfterCreate = sb.FreeBlocksCount;
        Assert.NotEqual(0u, sb.GetBlockPointer(dir, 1, true, out _));
        dir.Size = 2048; sb.WriteInode(dir);
        Assert.True(sb.InodeOps.Rmdir(sb.Root, "empty"));
        Assert.Equal(freeAfterCreate + 1, sb.FreeBlocksCount);
        var removed = sb.ReadInode(dir.InodeNumber, "removed");
        Assert.Equal(0u, removed.Blocks);
        Assert.True(removed.Dtime > 0);
    }

    [Fact]
    public void FreedInodeRecordsDeletionTimeAndZeroLinksOnDisk()
    {
        var (disk, sb) = Ext2AllocationTests.Mount(); var file = File(sb);
        file.Operations.Write(file, new byte[] { 1 });
        Resize(file, 0); sb.FreeInode(12);
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        var deleted = remounted!.ReadInode(12, "deleted");
        Assert.Equal(0, deleted.LinksCount);
        Assert.True(deleted.Dtime > 0);
        Assert.Equal(0u, deleted.Blocks);
    }

    [Fact]
    public void OverwriteUpdatesModificationAndChangeTimes()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        var file = File(sb);
        file.Operations.Write(file, new byte[] { 1 });
        var node = (Ext2Inode)file.Inode; node.Mtime = 1; node.Ctime = 1;
        file.Operations.Write(file, new byte[] { 2 });
        Assert.True(node.Mtime > 1); Assert.True(node.Ctime > 1);
    }
}
