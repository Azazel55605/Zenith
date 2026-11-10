using System;
using System.Diagnostics;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class Ext2ResizerTests
{
    /// <summary>A sparse image file that can be reopened larger, as an enlarged partition.</summary>
    private sealed class Image : IBlockDevice, IDisposable
    {
        public string Path { get; }
        private FileStream _file;
        public string Name => "resize-test";
        public ulong BlockSize => 512;
        public ulong BlockCount { get; private set; }
        public Image(ulong bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zenith-resize-" + Guid.NewGuid());
            _file = File.Open(Path, FileMode.CreateNew, FileAccess.ReadWrite);
            _file.SetLength((long)bytes); BlockCount = bytes / 512;
        }
        public void Enlarge(ulong bytes) { _file.SetLength((long)bytes); BlockCount = bytes / 512; }
        public void ReadBlock(ulong start, ulong count, Span<byte> data) { _file.Position = (long)(start * 512); _file.ReadExactly(data[..(int)(count * 512)]); }
        public void WriteBlock(ulong start, ulong count, ReadOnlySpan<byte> data) { _file.Position = (long)(start * 512); _file.Write(data[..(int)(count * 512)]); }
        public void Flush() => _file.Flush(true);
        public void Dispose() { _file.Dispose(); File.Delete(Path); }
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

    private static void WriteFile(Ext2Superblock sb, string name, string text)
    {
        Assert.True(sb.InodeOps.Create(sb.Root, name, VfsMode.OwnerRead | VfsMode.OwnerWrite, out var inode));
        var file = new OpenFile((Ext2Inode)inode!);
        Assert.Equal(text.Length, file.Operations.Write(file, System.Text.Encoding.ASCII.GetBytes(text)));
        sb.Drop();
    }

    private static string ReadFile(Ext2Superblock sb, string name)
    {
        Assert.True(sb.InodeOps.Lookup(sb.Root, name, out var inode));
        var file = new OpenFile((Ext2Inode)inode!);
        byte[] data = new byte[256];
        long n = file.Operations.Read(file, data);
        return System.Text.Encoding.ASCII.GetString(data, 0, (int)n);
    }

    private sealed class OpenFile(Ext2Inode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.Superblock.FileOps;
        public long Position { get; set; }
    }

    [Theory]
    [InlineData(600UL << 20, 3UL << 30, 0u)]     // within one descriptor block
    [InlineData(1UL << 30, 20UL << 30, 1u)]      // 8 -> 160 groups: the table grows into a reserved block
    public void LargeProfileGrowsAndKeepsData(ulong from, ulong to, uint descriptorGrowth)
    {
        using var disk = new Image(from);
        Ext2Formatter.Format(disk, "GROW");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        WriteFile(sb!, "kept.txt", "before the grow");
        disk.Flush();
        disk.Enlarge(to);
        Assert.Null(Ext2Resizer.Plan(disk, out var before, out var after));
        Assert.Equal(descriptorGrowth, after.DescriptorBlocks - before.DescriptorBlocks);
        Assert.Equal(before.ReservedGdt - descriptorGrowth, after.ReservedGdt);
        Ext2Resizer.Grow(disk);
        disk.Flush();
        Assert.True(Ext2Checker.Check(disk, out string report), report);
        Run("e2fsck", "-f", "-n", disk.Path);
        Assert.True(Ext2Superblock.TryCreate(disk, out var grown));
        Assert.Equal(after.Blocks, grown!.BlocksCount);
        Assert.Equal("before the grow", ReadFile(grown, "kept.txt"));
        // The new space is usable: a file whose data lands near the new end.
        Assert.True(grown.TryAllocateBlock(grown.GroupsCount - 1, out uint block));
        Assert.True(block >= before.Blocks);
        grown.FreeBlock(block);
        Assert.Contains("Reserved GDT blocks:      " + after.ReservedGdt, Run("debugfs", "-R", "stats", disk.Path));
    }

    [Fact]
    public void CompactProfileGrowsWithinItsDescriptorBlockOnly()
    {
        using var disk = new Image(64UL << 20);
        Ext2Formatter.Format(disk);
        disk.Enlarge(200UL << 20);
        Ext2Resizer.Grow(disk);
        disk.Flush();
        Assert.True(Ext2Checker.Check(disk, out string report), report);
        Run("e2fsck", "-f", "-n", disk.Path);
        // One 1 KiB descriptor block lists 32 groups of 8 MiB.
        disk.Enlarge(300UL << 20);
        Assert.Contains("cannot grow further", Ext2Resizer.Plan(disk, out _, out _));
    }

    [Fact]
    public void RefusesWithoutRoomOrOnAnUncleanVolume()
    {
        using var disk = new Image(600UL << 20);
        Ext2Formatter.Format(disk);
        Assert.Contains("enlarge the partition", Ext2Resizer.Plan(disk, out _, out _));
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        sb!.MarkMounted();
        disk.Enlarge(1UL << 30);
        Assert.NotNull(Ext2Resizer.Plan(disk, out _, out _));
        Assert.Throws<IOException>(() => Ext2Resizer.Grow(disk));
    }
}
