using System;
using System.Buffers.Binary;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Tests;

public class Ext2AllocationTests
{
    // Small synthetic one-group layout. Bitmap bit zero describes block ONE.
    private sealed class Disk : IBlockDevice
    {
        public readonly byte[] Data = new byte[32 * 1024];
        public string Name => "ext2-regression";
        public ulong BlockSize => 512;
        public ulong BlockCount => (ulong)Data.Length / BlockSize;
        public void ReadBlock(ulong start, ulong length, Span<byte> data) => Data.AsSpan(checked((int)(start * 512)), checked((int)(length * 512))).CopyTo(data);
        public void WriteBlock(ulong start, ulong length, ReadOnlySpan<byte> data) => data.Slice(0, checked((int)(length * 512))).CopyTo(Data.AsSpan(checked((int)(start * 512))));
        public void Flush() { }
        public void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(offset), value);
    }

    private static (Disk, Ext2Superblock) Mount(uint blocksPerGroup = 32)
    {
        var disk = new Disk();
        disk.Put(1024, 16); disk.Put(1028, 32); disk.Put(1036, 23); disk.Put(1040, 5);
        disk.Put(1044, 1); disk.Put(1056, blocksPerGroup); disk.Put(1060, blocksPerGroup); disk.Put(1064, 16);
        disk.Put(1080, 0x0001EF53); disk.Put(1100, 1); disk.Put(1108, 11); disk.Put(1112, 128); disk.Put(1120, 2);
        disk.Put(2048, 3); disk.Put(2052, 4); disk.Put(2056, 5);
        disk.Put(2060, (5u << 16) | 23u);
        disk.Put(2064, 1);
        // Blocks 1..8 allocated; block 8 contains the existing file's sentinel.
        disk.Data[3072] = 0xff;
        disk.Data[8 * 1024] = 0xa5;
        // Root inode #2 in table block 5.
        disk.Put(5 * 1024 + 128, 0x41ed);
        disk.Put(5 * 1024 + 128 + 4, 1024);
        disk.Put(5 * 1024 + 128 + 40, 7);
        Assert.True(Ext2Superblock.TryCreate(disk, out var sb));
        return (disk, sb!);
    }

    [Fact]
    public void AllocationPreservesExistingBlockAndFreeUsesSameBitmapBit()
    {
        var (disk, sb) = Mount();
        Assert.True(sb.TryAllocateBlock(0, out uint block));
        Assert.Equal(9u, block);
        Assert.Equal(0xa5, disk.Data[8 * 1024]);
        Assert.Equal(1, disk.Data[3073]); // bit 8 -> physical block 9
        Assert.Equal(22u, sb.FreeBlocksCount);
        sb.FreeBlock(block);
        Assert.Equal(0, disk.Data[3073]);
        Assert.Equal(23u, sb.FreeBlocksCount);
        Assert.True(sb.TryAllocateBlock(0, out uint reused));
        Assert.Equal(block, reused);
    }

    [Fact]
    public void GroupCountExcludesFirstDataBlock()
    {
        var (_, sb) = Mount(blocksPerGroup: 31);
        Assert.Equal(1u, sb.GroupsCount);
    }

    [Fact]
    public void FinalGroupCannotAllocateOnePastVolume()
    {
        var (disk, sb) = Mount();
        disk.Data.AsSpan(3072, 1024).Fill(0xff);
        disk.Data[3075] = 0x3f; // free bits 30 (block 31) and 31 (outside volume)
        Assert.True(sb.TryAllocateBlock(0, out uint last));
        Assert.Equal(31u, last);
        Assert.False(sb.TryAllocateBlock(0, out _));
        uint free = sb.FreeBlocksCount;
        sb.FreeBlock(0); sb.FreeBlock(32);
        Assert.Equal(free, sb.FreeBlocksCount);
    }
}
