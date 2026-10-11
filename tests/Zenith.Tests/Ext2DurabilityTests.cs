using System;
using System.Buffers.Binary;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class Ext2DurabilityTests
{
    private sealed class VolatileDisk : IBlockDevice
    {
        private readonly Ext2AllocationTests.Disk _inner = new(1024);
        public byte[] Durable = Array.Empty<byte>();
        public string Name => "volatile-ext2";
        public ulong BlockSize => _inner.BlockSize;
        public ulong BlockCount => _inner.BlockCount;
        public void ReadBlock(ulong start, ulong count, Span<byte> buffer) => _inner.ReadBlock(start, count, buffer);
        public void WriteBlock(ulong start, ulong count, ReadOnlySpan<byte> buffer) => _inner.WriteBlock(start, count, buffer);
        public void Flush() => Durable = (byte[])_inner.Data.Clone();
    }

    [Fact]
    public void MountIsDurablyUncleanUntilDropAndCheckMakesNoChanges()
    {
        var disk = new VolatileDisk();
        Ext2Formatter.Format(disk, "DURABLE");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        sb!.MarkMounted();
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(disk.Durable.AsSpan(1024 + 58)));
        Assert.NotNull(Ext2VolumePolicy.Check(disk));
        byte[] before = (byte[])disk.Durable.Clone();
        Assert.False(Ext2Checker.Check(disk, out _));
        Assert.Equal(before, disk.Durable);
        Assert.True(sb.SuperOps.Sync(sb));
        Assert.NotNull(Ext2VolumePolicy.Check(disk));
        sb.SuperOps.Drop(sb);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(disk.Durable.AsSpan(1024 + 58)));
        Assert.Null(Ext2VolumePolicy.Check(disk));
        Assert.True(Ext2Checker.Check(disk, out var report), report);
    }

    [Fact]
    public void SyncMakesLatestSuperblockCountersDurable()
    {
        var disk = new VolatileDisk();
        Ext2Formatter.Format(disk, "DURABLE");
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        uint latest = sb!.FreeBlocksCount - 1;
        sb.FreeBlocksCount = latest;
        Assert.True(sb.SuperOps.Sync(sb));
        Assert.Equal(latest, BinaryPrimitives.ReadUInt32LittleEndian(disk.Durable.AsSpan(1024 + 12)));
    }
}
