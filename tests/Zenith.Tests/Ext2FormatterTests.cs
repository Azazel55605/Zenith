using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public sealed class Ext2FormatterTests
{
    private sealed class Disk : IBlockDevice, IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zenith-mkfs-" + Guid.NewGuid());
        private readonly FileStream _file;
        public string Name => "formatter-test";
        public ulong BlockSize { get; }
        public ulong BlockCount { get; }
        public int Writes { get; private set; }
        public Disk(ulong bytes, ulong sector = 512)
        {
            BlockSize = sector;
            BlockCount = bytes / sector;
            _file = File.Open(Path, FileMode.CreateNew, FileAccess.ReadWrite);
            _file.SetLength((long)bytes);
        }
        public void ReadBlock(ulong start, ulong count, Span<byte> data)
        {
            Bounds(start, count, data.Length);
            _file.Position = (long)(start * BlockSize);
            _file.ReadExactly(data[..(int)(count * BlockSize)]);
        }
        public void WriteBlock(ulong start, ulong count, ReadOnlySpan<byte> data)
        {
            Bounds(start, count, data.Length);
            _file.Position = (long)(start * BlockSize);
            _file.Write(data[..(int)(count * BlockSize)]);
            Writes++;
        }
        private void Bounds(ulong start, ulong count, int bytes)
        {
            Assert.True(start <= BlockCount && count <= BlockCount - start);
            Assert.True((ulong)bytes >= count * BlockSize);
        }
        public void Flush() => _file.Flush(true);
        public void Dispose() { _file.Dispose(); File.Delete(Path); }
        public byte[] Read(uint block)
        {
            byte[] bytes = new byte[1024];
            ReadBlock(block * (1024 / BlockSize), 1024 / BlockSize, bytes);
            return bytes;
        }
    }

    [Theory]
    [InlineData(1024, 512)]
    [InlineData(8192, 512)]
    [InlineData(8193, 1024)]
    [InlineData(16384, 512)]
    [InlineData(17408, 1024)]
    [InlineData(270336, 512)] // 33 groups: descriptor table spans two blocks
    public void FormattedVolumesMountAndPassIndependentFsck(int kib, int sector)
    {
        using var disk = new Disk((ulong)kib * 1024, (ulong)sector);
        Ext2Formatter.Format(disk, "TEST");
        Assert.Null(Ext2VolumePolicy.Check(disk));
        Assert.True(Ext2Superblock.TryCreate(disk, out var mounted));
        Assert.Equal(3, mounted!.ReadInode(2, "/").LinksCount);
        Assert.True(mounted.Root.InodeOperations.Lookup(mounted.Root, "lost+found", out var lost));
        Assert.NotNull(lost);
        uint before = mounted.FreeBlocksCount;
        Assert.True(mounted.TryAllocateBlock(mounted.GroupsCount - 1, out uint allocated));
        mounted.FreeBlock(allocated);
        Assert.Equal(before, mounted.FreeBlocksCount);
        Assert.True(Ext2Superblock.TryCreate(disk, out var remounted));
        Assert.Equal(before, remounted!.FreeBlocksCount);
        for (uint group = 1; group < mounted.GroupsCount; group++)
        {
            byte[] backup = disk.Read(1 + group * 8192);
            Assert.Equal(0xef53, BinaryPrimitives.ReadUInt16LittleEndian(backup.AsSpan(56)));
            Assert.Equal(group, BinaryPrimitives.ReadUInt16LittleEndian(backup.AsSpan(90)));
        }
        disk.Flush();
        var options = new ProcessStartInfo("e2fsck") { RedirectStandardOutput = true, RedirectStandardError = true };
        options.ArgumentList.Add("-f"); options.ArgumentList.Add("-n"); options.ArgumentList.Add(disk.Path);
        using var check = Process.Start(options)!;
        string output = check.StandardOutput.ReadToEnd() + check.StandardError.ReadToEnd();
        Assert.True(check.WaitForExit(30000), "e2fsck timed out");
        Assert.True(check.ExitCode == 0, output);
    }

    [Fact]
    public void SequentialAllocationDoesNotAllocateScratchBuffersPerBlock()
    {
        using var disk = new Disk(16 * 1024 * 1024);
        Ext2Formatter.Format(disk);
        Assert.True(Ext2Superblock.TryCreate(disk, out var mounted));
        Assert.True(mounted!.TryAllocateBlock(0, out _)); // warm I/O buffers
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++)
        {
            Assert.True(mounted.TryAllocateBlock(0, out _));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, "Allocator scratch churn: " + allocated + " bytes for 1024 blocks");
    }

    [Theory]
    [InlineData(512, 512, "")]
    [InlineData(8194, 512, "")]
    [InlineData(1024, 2048, "")]
    [InlineData(525312, 512, "")]
    [InlineData(1024, 512, "seventeenletters!")]
    [InlineData(1024, 512, "ümlaut")]
    public void InvalidGeometryAndLabelsDoNotWrite(int kib, int sector, string label)
    {
        using var disk = new Disk((ulong)kib * 1024, (ulong)sector);
        Assert.NotNull(Ext2Formatter.Check(disk, label));
        Assert.Throws<IOException>(() => Ext2Formatter.Format(disk, label));
        Assert.Equal(0, disk.Writes);
    }

    [Fact]
    public void FailedMetadataWritesLeavePrimaryUnrecognized()
    {
        using var disk = new Disk(1024 * 1024);
        Ext2Formatter.Format(disk);
        // Reformat over a valid old signature, with failure after invalidating it.
        using var failing = new FailingAfterInvalidate(disk);
        Assert.Throws<IOException>(() => Ext2Formatter.Format(failing));
        Assert.NotNull(Ext2VolumePolicy.Check(disk));
    }

    private sealed class FailingAfterInvalidate(Disk disk) : IBlockDevice, IDisposable
    {
        private int _writes;
        public string Name => disk.Name;
        public ulong BlockSize => disk.BlockSize;
        public ulong BlockCount => disk.BlockCount;
        public void ReadBlock(ulong start, ulong count, Span<byte> data) => disk.ReadBlock(start, count, data);
        public void WriteBlock(ulong start, ulong count, ReadOnlySpan<byte> data)
        {
            if (_writes++ != 0)
            {
                throw new IOException("injected metadata failure");
            }
            disk.WriteBlock(start, count, data);
        }
        public void Flush() => disk.Flush();
        public void Dispose() { }
    }
}
