using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Zenith.Core.Storage.Dev;

namespace Zenith.Tests;

public class BlockDeviceTests
{
    private sealed class Disk(string name = "sata0", ulong blockSize = 16, ulong count = 4) : IBlockDevice
    {
        public string Name => name;
        public ulong BlockSize => blockSize;
        public ulong BlockCount => count;
        public List<(ulong Start, ulong Count)> Reads { get; } = new();
        public Action? AfterRead;
        public bool FailRead;
        public int Writes;
        public void ReadBlock(ulong start, ulong length, Span<byte> data)
        {
            Assert.True(start <= BlockCount && length <= BlockCount - start);
            Assert.True((ulong)data.Length >= length * BlockSize);
            if (FailRead) throw new IOException("removed or failed");
            Reads.Add((start, length));
            for (int i = 0; i < (int)(length * BlockSize); i++)
            {
                data[i] = (byte)((start * BlockSize + (ulong)i) % 251);
            }
            AfterRead?.Invoke();
        }
        public void WriteBlock(ulong start, ulong length, ReadOnlySpan<byte> data) => Writes++;
        public void Flush() { }
    }

    private sealed class PartitionView(Disk host) : IBlockDevice
    {
        public string Name => "sata0p1";
        public ulong BlockSize => host.BlockSize;
        public ulong BlockCount => 2;
        public void ReadBlock(ulong start, ulong length, Span<byte> data)
        {
            Assert.True(start <= BlockCount && length <= BlockCount - start);
            host.ReadBlock(3 + start, length, data);
        }
        public void WriteBlock(ulong start, ulong length, ReadOnlySpan<byte> data) => host.WriteBlock(3 + start, length, data);
        public void Flush() => host.Flush();
    }

    private sealed class OpenFile(IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get; set; }
    }

    private static IVfsSuperblock Mount(Func<IReadOnlyList<IBlockDevice>> devices)
    {
        Assert.True(new DevFilesystemType(devices).TryMount("", MountFlags.None, out var sb));
        return sb;
    }

    private static OpenFile Open(IVfsSuperblock sb, string name = "sata0")
    {
        Assert.True(sb.Root.InodeOperations.Lookup(sb.Root, name, out var node));
        return new OpenFile(node);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(3, 45)]
    [InlineData(15, 2)]
    [InlineData(47, 40)]
    [InlineData(63, 8)]
    [InlineData(64, 8)]
    public void ByteReadsRespectSectorAndDeviceBounds(int position, int requested)
    {
        var disk = new Disk();
        var file = Open(Mount(() => new[] { disk }));
        file.Position = position;
        byte[] result = Enumerable.Repeat((byte)255, requested + 2).ToArray();
        int expected = Math.Min(requested, 64 - position);
        Assert.Equal(expected, file.Operations.Read(file, result.AsSpan(1, requested)));
        Assert.Equal(position, file.Position);
        Assert.Equal(255, result[0]);
        Assert.Equal(255, result[^1]);
        for (int i = 0; i < expected; i++) Assert.Equal((byte)(position + i), result[i + 1]);
        for (int i = expected; i < requested; i++) Assert.Equal(255, result[i + 1]);
        if (position == 0) Assert.Equal(new[] { (0ul, 2ul) }, disk.Reads);
        if (position == 64) Assert.Empty(disk.Reads);
    }

    [Fact]
    public void PartitionReadUsesItsOwnCapacityAndTranslatedSectorView()
    {
        var host = new Disk(count: 8);
        var partition = new PartitionView(host);
        var file = Open(Mount(() => new IBlockDevice[] { host, partition }), "sata0p1");
        Assert.True(file.Operations.Seek(file, -1, SeekWhence.End, out var position));
        Assert.Equal(31, position);
        byte[] data = new byte[8];
        Assert.Equal(1, file.Operations.Read(file, data));
        Assert.Equal(79, data[0]); // Partition starts at host byte 48, ends at byte 79.
        Assert.Equal(new[] { (4ul, 1ul) }, host.Reads);
        file.Position += 1;
        Assert.Equal(0, file.Operations.Read(file, data));
        Assert.Single(host.Reads);
    }

    [Fact]
    public void ZeroLengthNegativePositionAndBeyondEofNeverReadDisk()
    {
        var disk = new Disk();
        var file = Open(Mount(() => new[] { disk }));
        Assert.Equal(0, file.Operations.Read(file, Span<byte>.Empty));
        file.Position = long.MaxValue;
        Assert.Equal(0, file.Operations.Read(file, new byte[8]));
        file.Position = -1;
        Assert.Throws<IOException>(() => file.Operations.Read(file, new byte[1]));
        Assert.Empty(disk.Reads);
    }

    [Fact]
    public void SeekHandlesAllOriginsAndRejectsOverflowAndOutOfBounds()
    {
        var disk = new Disk();
        var file = Open(Mount(() => new[] { disk }));
        Assert.True(file.Operations.Seek(file, 5, SeekWhence.Set, out var pos));
        Assert.Equal(5, pos);
        Assert.True(file.Operations.Seek(file, 3, SeekWhence.Cur, out pos));
        Assert.Equal(8, pos);
        Assert.True(file.Operations.Seek(file, -1, SeekWhence.End, out pos));
        Assert.Equal(63, pos);
        Assert.False(file.Operations.Seek(file, long.MaxValue, SeekWhence.Cur, out _));
        Assert.False(file.Operations.Seek(file, long.MinValue, SeekWhence.End, out _));
        Assert.False(file.Operations.Seek(file, 65, SeekWhence.Set, out _));
        Assert.False(file.Operations.Seek(file, 0, (SeekWhence)99, out _));
        Assert.Equal(63, file.Position);
        Assert.True(file.Operations.Seek(file, 0, SeekWhence.End, out pos));
        Assert.Equal(64, pos);
    }

    [Fact]
    public void MetadataReportsCapacityAndRejectsEveryWriteAndResize()
    {
        var disk = new Disk();
        var sb = Mount(() => new[] { disk });
        var file = Open(sb);
        var ops = file.Inode.InodeOperations;
        Assert.True(ops.GetAttr(file.Inode, out var stat));
        Assert.Equal(VfsMode.BlockDevice, stat.Mode & VfsMode.FileTypeMask);
        Assert.False(stat.IsDirectory);
        Assert.False(stat.IsRegularFile);
        Assert.Equal((VfsMode)0x124, stat.Mode & VfsMode.PermissionMask);
        Assert.Equal(64ul, stat.Size);
        Assert.Equal(16, stat.BlkSize);
        Assert.NotEqual(0ul, stat.Rdev);
        Assert.False(ops.SetAttr(file.Inode, SetAttrFlags.Size, new VfsStat()));
        Assert.False(ops.SetAttr(file.Inode, SetAttrFlags.Mode, stat));
        Assert.Throws<IOException>(() => file.Operations.Write(file, new byte[4]));
        Assert.Throws<IOException>(() => file.Operations.Write(file, ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, disk.Writes);
        Assert.True(sb.SuperOperations.StatFs(sb, out var fs));
        Assert.Equal(4ul, fs.Files);
    }

    [Fact]
    public void LiveNamespacePreservesIdentityAndInvalidatesRemovedAndReplacedHandles()
    {
        var disk = new Disk();
        IReadOnlyList<IBlockDevice> devices = new[] { disk };
        var sb = Mount(() => devices);
        var file = Open(sb);
        var same = Open(sb);
        Assert.Same(file.Inode, same.Inode);
        Assert.True(file.Inode.InodeOperations.GetAttr(file.Inode, out var oldStat));
        devices = Array.Empty<IBlockDevice>();
        Assert.False(sb.Root.InodeOperations.Lookup(sb.Root, "sata0", out _));
        Assert.False(file.Inode.InodeOperations.GetAttr(file.Inode, out _));
        Assert.Throws<IOException>(() => file.Operations.Read(file, new byte[1]));
        Assert.Throws<IOException>(() => file.Operations.Seek(file, 0, SeekWhence.Set, out _));
        Assert.Throws<IOException>(() => file.Operations.Fsync(file));
        var replacement = new Disk();
        devices = new[] { replacement };
        var next = Open(sb);
        Assert.NotSame(file.Inode, next.Inode);
        Assert.True(next.Inode.InodeOperations.GetAttr(next.Inode, out var newStat));
        Assert.NotEqual(oldStat.Ino, newStat.Ino);
        Assert.Throws<IOException>(() => file.Operations.Read(file, new byte[1]));
        Assert.Equal(1, next.Operations.Read(next, new byte[1]));
    }

    [Fact]
    public void RemovalBetweenSectorCallsAndDeviceFailuresPropagate()
    {
        var disk = new Disk();
        IReadOnlyList<IBlockDevice> devices = new[] { disk };
        var file = Open(Mount(() => devices));
        file.Position = 1;
        disk.AfterRead = () => devices = Array.Empty<IBlockDevice>();
        Assert.Throws<IOException>(() => file.Operations.Read(file, new byte[32]));
        Assert.Single(disk.Reads);
        devices = new[] { disk };
        disk.AfterRead = null;
        disk.FailRead = true;
        Assert.Throws<IOException>(() => file.Operations.Read(file, new byte[1]));
    }

    [Theory]
    [InlineData(0ul, 4ul)]
    [InlineData(2097152ul, 4ul)]
    [InlineData(512ul, ulong.MaxValue)]
    public void InvalidGeometryIsNotPublished(ulong blockSize, ulong count)
    {
        var sb = Mount(() => new[] { new Disk("sata0", blockSize, count) });
        Assert.False(sb.Root.InodeOperations.Lookup(sb.Root, "sata0", out _));
    }

    [Fact]
    public void ReservedInvalidAndDuplicateNamesAreNotPublished()
    {
        var disk = new Disk();
        var sb = Mount(() => new IBlockDevice[] { disk, disk, new Disk("null"), new Disk("../bad"), new Disk(""), new Disk("."), new Disk("a\\b") });
        Assert.True(sb.Root.InodeOperations.ReadDir(sb.Root, out var entries));
        Assert.Equal(new[] { "null", "zero", "sata0" }, entries.Select(n => n.Name));
    }

    [Fact]
    public void LargeCapacityReadsAtLastSectorWithoutIntegerOverflow()
    {
        var disk = new Disk(blockSize: 4096, count: (ulong)long.MaxValue / 4096);
        var file = Open(Mount(() => new[] { disk }));
        long length = (long)(disk.BlockSize * disk.BlockCount);
        Assert.True(file.Operations.Seek(file, -2, SeekWhence.End, out _));
        byte[] data = new byte[8];
        Assert.Equal(2, file.Operations.Read(file, data));
        Assert.Equal((byte)((ulong)(length - 2) % 251), data[0]);
        Assert.Equal((byte)((ulong)(length - 1) % 251), data[1]);
        Assert.Equal(disk.BlockCount - 1, disk.Reads.Single().Start);
    }
}
