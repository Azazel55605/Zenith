using System;
using System.Diagnostics;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class Ext2MetadataTests
{
    [Theory]
    [InlineData(0u, 0u)] [InlineData(65535u, 65536u)]
    [InlineData(70000u, 80000u)] [InlineData(uint.MaxValue, uint.MaxValue)]
    public void OwnershipAndModeSurviveRemountAndIndependentInspection(uint uid, uint gid)
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "META");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.True(sb!.InodeOps.Create(sb.Root, "file", VfsMode.OwnerRead, out var inode));
        var attributes = new VfsStat { Uid = uid, Gid = gid, Mode = (VfsMode)0xa9e8 };
        Assert.True(sb.InodeOps.SetAttr(inode!, SetAttrFlags.Mode | SetAttrFlags.Uid | SetAttrFlags.Gid, attributes));
        disk.Flush();
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        Assert.True(remounted!.InodeOps.Lookup(remounted.Root, "file", out var reopened));
        Assert.True(remounted.InodeOps.GetAttr(reopened!, out var stat));
        Assert.Equal(uid, stat.Uid); Assert.Equal(gid, stat.Gid);
        Assert.Equal(0x89e8, (int)stat.Mode); // preserve file type, apply 4750
        string external = Run("debugfs", "-R", "stat /file", disk.Path);
        // debugfs prints these unsigned fields through signed decimal formatting.
        Assert.Matches(@"User:\s+" + unchecked((int)uid) + @"\s+Group:\s+" + unchecked((int)gid) + @"\s", external);
        Assert.Contains("Mode:  04750", external);
        Run("e2fsck", "-f", "-n", disk.Path);
    }

    [Fact]
    public void AttributeFlagsPreserveOtherFieldsAndUpdateChangeTime()
    {
        var (_, sb) = Ext2AllocationTests.Mount();
        var inode = new Ext2Inode(sb, 12, "file") { Mode = 0x81a4, Uid = 70000, Gid = 80000, Mtime = 456, Ctime = 123 };
        sb.WriteInode(inode);
        var attributes = new VfsStat { Uid = 123456, Gid = 0, Mode = 0 };
        Assert.True(sb.InodeOps.SetAttr(inode, SetAttrFlags.Uid, attributes));
        Assert.Equal(123456u, inode.Uid); Assert.Equal(80000u, inode.Gid);
        Assert.Equal(0x81a4, inode.Mode); Assert.Equal(456u, inode.Mtime);
        Assert.True(inode.Ctime > 123);
    }

    [Fact]
    public void InvalidCombinedAttributesAreRejectedBeforeTruncationOrModeChange()
    {
        var (disk, sb) = Ext2AllocationTests.Mount();
        var inode = new Ext2Inode(sb, 12, "file") { Mode = 0x81a4, Size = 12 };
        sb.WriteInode(inode);
        byte[] before = (byte[])disk.Data.Clone();
        var attributes = new VfsStat { Size = 0, Mode = 0, Atime = new VfsTimespec(-1, 0) };
        Assert.False(sb.InodeOps.SetAttr(inode, SetAttrFlags.Size | SetAttrFlags.Mode | SetAttrFlags.Atime, attributes));
        Assert.False(sb.InodeOps.SetAttr(inode, (SetAttrFlags)128, attributes));
        Assert.True(sb.InodeOps.SetAttr(inode, SetAttrFlags.None, attributes));
        Assert.Equal(before, disk.Data);
        Assert.Equal(12u, inode.Size); Assert.Equal(0x81a4, inode.Mode);
    }

    [Fact]
    public void ReusedInodeDoesNotKeepHighOwnershipBits()
    {
        using var disk = new Ext2FormatterTests.Disk(1024 * 1024);
        Ext2Formatter.Format(disk, "META");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        Assert.True(sb!.InodeOps.Create(sb.Root, "old", VfsMode.OwnerRead, out var old));
        Assert.True(sb.InodeOps.SetAttr(old!, SetAttrFlags.Uid | SetAttrFlags.Gid, new VfsStat { Uid = 70000, Gid = 80000 }));
        uint number = Assert.IsType<Ext2Inode>(old).InodeNumber;
        Assert.True(sb.InodeOps.Unlink(sb.Root, "old"));
        Assert.True(sb.InodeOps.Create(sb.Root, "new", VfsMode.OwnerRead, out var created));
        Assert.Equal(number, Assert.IsType<Ext2Inode>(created).InodeNumber);
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        Assert.True(remounted!.InodeOps.Lookup(remounted.Root, "new", out var reopened));
        Assert.True(remounted.InodeOps.GetAttr(reopened!, out var stat));
        Assert.Equal(0u, stat.Uid); Assert.Equal(0u, stat.Gid);
    }

    private static string Run(string command, params string[] args)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.True(process.ExitCode == 0, output);
        return output;
    }
}
