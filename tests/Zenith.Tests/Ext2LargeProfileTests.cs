using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

/// <summary>The 4 KiB profile (volumes over 512 MiB, up to 2 TiB) and files over 4 GiB.</summary>
public class Ext2LargeProfileTests
{
    private sealed class OpenFile(Ext2Inode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.Superblock.FileOps;
        public long Position { get; set; }
    }

    private static string Run(string program, params string[] arguments)
    {
        var options = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { options.ArgumentList.Add(argument); }
        using var process = Process.Start(options)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120000), program + " timed out");
        Assert.True(process.ExitCode == 0, program + ": " + output);
        return output;
    }

    [Theory]
    [InlineData(600UL << 20, 512UL)]      // just over the compact profile: five groups, the last one dropped if tiny
    [InlineData(2UL << 30, 512UL)]
    [InlineData(20UL << 30, 4096UL)]     // 160 groups, 4 KiB sectors: two descriptor blocks
    public void LargeVolumesFormatMountAndPassIndependentFsck(ulong bytes, ulong sector)
    {
        using var disk = new Ext2FormatterTests.Disk(bytes, sector);
        Ext2Formatter.Format(disk, "LARGE");
        Assert.Null(Ext2VolumePolicy.Check(disk));
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.Equal(4096u, sb!.BlockSize);
        Assert.Equal(0u, sb.FirstDataBlock);
        Assert.Equal(Ext2SuperblockLayout.RoCompatSparseSuper | Ext2SuperblockLayout.RoCompatLargeFile, sb.FeatureRoCompat);
        Assert.True(sb.Root.InodeOperations.Lookup(sb.Root, "lost+found", out _));
        // Backup superblocks exist exactly in groups 0, 1 and powers of 3, 5 and 7.
        byte[] block = new byte[4096];
        for (uint group = 1; group < sb.GroupsCount; group++)
        {
            Ext2Superblock.ReadBlocks(disk, group * 32768, 1, 4096, disk.BlockSize, block);
            bool backup = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(56)) == 0xef53;
            Assert.Equal(Ext2Geometry.HasBackup(group, true), backup);
            if (backup) { Assert.Equal(group, BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(90))); }
        }
        Assert.True(Ext2Checker.Check(disk, out string report), report);
        disk.Flush();
        Run("e2fsck", "-f", "-n", disk.Path);
    }

    [Fact]
    public void FilesPastFourGibUseTripleIndirectBlocksAndTruncateCleanly()
    {
        using var disk = new Ext2FormatterTests.Disk(6UL << 30);
        Ext2Formatter.Format(disk, "BIG");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.True(sb!.InodeOps.Create(sb.Root, "big", VfsMode.OwnerRead | VfsMode.OwnerWrite, out var created));
        var file = new OpenFile((Ext2Inode)created!);
        long[] offsets = { 0, 48L << 10, 4L << 20, (4L << 30) + (4L << 20) + (64L << 10), (5L << 30) + 123 };
        foreach (long offset in offsets)
        {
            file.Position = offset;
            byte[] data = BitConverter.GetBytes(offset);
            Assert.Equal(data.Length, file.Operations.Write(file, data));
        }
        var inode = (Ext2Inode)file.Inode;
        Assert.Equal((ulong)((5L << 30) + 123 + 8), inode.FullSize);
        Assert.NotEqual(0u, inode.Block[Ext2InodeLayout.TripleIndirectIndex]);
        disk.Flush();
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        Assert.True(remounted!.InodeOps.Lookup(remounted.Root, "big", out var found));
        var reopened = new OpenFile((Ext2Inode)found!);
        foreach (long offset in offsets)
        {
            reopened.Position = offset;
            byte[] back = new byte[8];
            Assert.Equal(8, reopened.Operations.Read(reopened, back));
            Assert.Equal(offset, BitConverter.ToInt64(back));
        }
        Assert.True(Ext2Checker.Check(disk, out string report), report);
        Assert.Contains("Size: " + ((5L << 30) + 131), Run("debugfs", "-R", "stat /big", disk.Path));
        Run("e2fsck", "-f", "-n", disk.Path);
        // Truncating below the triple-indirect range frees its whole tree.
        uint free = remounted.FreeBlocksCount;
        var stat = new VfsStat { Size = 1UL << 20 };
        Assert.True(found!.InodeOperations.SetAttr(found, SetAttrFlags.Size, stat));
        Assert.Equal(0u, ((Ext2Inode)found).Block[Ext2InodeLayout.TripleIndirectIndex]);
        Assert.True(remounted.FreeBlocksCount > free);
        disk.Flush();
        Assert.True(Ext2Checker.Check(disk, out report), report);
        Run("e2fsck", "-f", "-n", disk.Path);
    }

    [Fact]
    public void CompactVolumesStayBelowTwoGibPerFile()
    {
        using var disk = new Ext2FormatterTests.Disk(64UL << 20);
        Ext2Formatter.Format(disk);
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.Equal(1024u, sb!.BlockSize);
        Assert.Equal((1UL << 31) - 1, sb.MaxFileSize);
    }

    [Fact]
    public void VolumesOverTwoTibAreRefusedWithoutWrites()
    {
        using var disk = new Ext2FormatterTests.Disk((2UL << 40) + 4096);
        Assert.Contains("2 TiB", Ext2Formatter.Check(disk, ""));
        Assert.Throws<IOException>(() => Ext2Formatter.Format(disk));
        Assert.Equal(0, disk.Writes);
    }

    [Fact]
    public void CheckRefusesWhenMemoryIsShort()
    {
        using var disk = new Ext2FormatterTests.Disk(2UL << 30);
        Ext2Formatter.Format(disk);
        var previous = Ext2Checker.AvailableMemory;
        try
        {
            Ext2Checker.AvailableMemory = () => 1024;
            Assert.False(Ext2Checker.Check(disk, out string report));
            Assert.Contains("free memory", report);
        }
        finally { Ext2Checker.AvailableMemory = previous; }
    }

    [Theory]
    [InlineData(600UL << 20, 5u, 4096u)]
    [InlineData(64UL << 30, 512u, 4096u)]
    [InlineData(2UL << 40, 16384u, 256u)]
    public void GeometryScalesInodesWithTheVolume(ulong bytes, uint groups, uint inodesPerGroup)
    {
        var geometry = Ext2Geometry.For(bytes)!.Value;
        Assert.Equal(groups, geometry.Groups);
        Assert.Equal(inodesPerGroup, geometry.InodesPerGroup);
        Assert.True(Ext2Checker.RequiredMemory(geometry.Blocks, geometry.Inodes) < 160UL << 20);
    }
}
