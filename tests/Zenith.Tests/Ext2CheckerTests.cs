using System;
using Zenith.Core.Storage.Ext2;
using Cosmos.Kernel.HAL.Vfs;

namespace Zenith.Tests;

public class Ext2CheckerTests
{
    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(16 * 1024 * 1024)]
    public void CleanFormattedVolumeIsReadOnlyAndValid(int bytes)
    {
        using var disk = new Ext2FormatterTests.Disk((ulong)bytes);
        Ext2Formatter.Format(disk, "CHECK");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.True(sb!.InodeOps.Mkdir(sb.Root, "nested", (VfsMode)0x1ed, out var nested));
        Assert.True(sb.InodeOps.Create(nested!, "file", VfsMode.OwnerRead, out _));
        Assert.True(sb.InodeOps.Symlink(sb.Root, "link", "nested/file", out _));
        int writes = disk.Writes;
        Assert.True(Ext2Checker.Check(disk, out var report), report);
        Assert.Contains("no changes made", report);
        Assert.Equal(writes, disk.Writes);
    }

    [Theory]
    [InlineData("links")]
    [InlineData("counts")]
    [InlineData("record")]
    [InlineData("bitmap")]
    [InlineData("parent")]
    [InlineData("duplicate")]
    [InlineData("root-link")]
    [InlineData("large-file")]
    public void CorruptVolumesAreReportedWithoutWrites(string corruption)
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "CHECK");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        var root = (Ext2Inode)sb!.Root;
        if (corruption == "links") { root.LinksCount++; sb.WriteInode(root); }
        else if (corruption == "counts") { sb.FreeBlocksCount++; sb.UpdateSuperblock(); }
        else if (corruption == "bitmap")
        {
            var gd = sb.Group(0);
            byte[] map = disk.Read(gd.BlockBitmap);
            map[0] ^= 1; sb.WriteBlocks(gd.BlockBitmap, 1, map);
        }
        else if (corruption == "duplicate")
        {
            Assert.True(sb.InodeOps.Create(root, "file", VfsMode.OwnerRead, out var file));
            ((Ext2Inode)file!).Block[0] = root.Block[0]; ((Ext2Inode)file).Blocks = 2;
            sb.WriteInode((Ext2Inode)file);
        }
        else if (corruption == "root-link")
        {
            Assert.True(Ext2DirectoryHelper.AddEntry(sb, root, "cycle", 2, Ext2InodeLayout.FileTypeDir));
            root.LinksCount++; sb.WriteInode(root);
        }
        else if (corruption == "large-file")
        {
            Assert.True(sb.InodeOps.Create(root, "large", VfsMode.OwnerRead, out var file));
            ((Ext2Inode)file!).Size = (uint)sb.MaxFileSize + 1;
            sb.WriteInode((Ext2Inode)file);
        }
        else
        {
            byte[] block = disk.Read(root.Block[0]);
            if (corruption == "record") { block[4] = 0; block[5] = 0; }
            else { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), 11); }
            sb.WriteBlocks(root.Block[0], 1, block);
        }
        int writes = disk.Writes;
        Assert.False(Ext2Checker.Check(disk, out var report));
        Assert.Contains("no changes made", report);
        Assert.Equal(writes, disk.Writes);
    }
}
