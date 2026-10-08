using System;
using System.Buffers.Binary;
using Zenith.Core.Storage.Ext2;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Tests;

public class Ext2VolumePolicyTests
{
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static byte[] Valid()
    {
        byte[] data = new byte[1024];
        Put(data, 0, 2048); Put(data, 4, 8192); Put(data, 12, 7000); Put(data, 16, 2000);
        Put(data, 20, 1); Put(data, 32, 8192); Put(data, 36, 8192); Put(data, 40, 2048);
        Put(data, 56, 0x0001EF53); Put(data, 76, 1); Put(data, 88, 128); Put(data, 96, 2);
        return data;
    }

    private sealed class Disk(ulong sectorSize, ulong count) : IBlockDevice
    {
        public string Name => "fixture";
        public ulong BlockSize => sectorSize;
        public ulong BlockCount => count;
        public int Reads;
        public void ReadBlock(ulong start, ulong length, Span<byte> data)
        {
            Assert.Equal(1024UL / sectorSize, start);
            Assert.Equal(1024UL / sectorSize, length);
            Assert.Equal(1024, data.Length);
            Valid().CopyTo(data);
            Reads++;
        }
        public void WriteBlock(ulong start, ulong length, ReadOnlySpan<byte> data) => Assert.Fail("profile check must not write");
        public void Flush() => Assert.Fail("profile check must not flush");
    }

    [Theory]
    [InlineData(512UL)] [InlineData(1024UL)]
    public void ReadsOnlySuperblockAtByte1024(ulong sectorSize)
    {
        var disk = new Disk(sectorSize, 8 * 1024 * 1024 / sectorSize);
        Assert.Null(Ext2VolumePolicy.Check(disk));
        Assert.Equal(1, disk.Reads);
    }

    [Theory]
    [InlineData(0UL, 100UL)] [InlineData(4096UL, 2048UL)]
    [InlineData(512UL, 3UL)] [InlineData(512UL, ulong.MaxValue)]
    public void RejectsDeviceGeometryBeforeReading(ulong sectorSize, ulong count)
    {
        var disk = new Disk(sectorSize, count);
        Assert.Contains("geometry", Ext2VolumePolicy.Check(disk));
        Assert.Equal(0, disk.Reads);
    }

    [Fact]
    public void AcceptsConservativeProfile()
    {
        Assert.Null(Ext2VolumePolicy.CheckSuperblock(Valid(), 8 * 1024 * 1024));
    }

    [Theory]
    [InlineData(76, 2u)] [InlineData(24, 2u)] [InlineData(88, 256u)]
    [InlineData(92, 4u)] [InlineData(96, 0x42u)] [InlineData(100, 0x400u)]
    public void RejectsUnsupportedProfiles(int offset, uint value)
    {
        byte[] data = Valid(); Put(data, offset, value);
        Assert.Contains("unsupported", Ext2VolumePolicy.CheckSuperblock(data, 8 * 1024 * 1024));
    }

    [Theory]
    [InlineData(4, uint.MaxValue)] [InlineData(20, 0u)] [InlineData(32, 0u)]
    [InlineData(40, 8193u)] [InlineData(12, 8193u)] [InlineData(16, 2049u)]
    [InlineData(28, 1u)] [InlineData(36, 1u)]
    public void RejectsInvalidGeometry(int offset, uint value)
    {
        byte[] data = Valid(); Put(data, offset, value);
        Assert.Contains("geometry", Ext2VolumePolicy.CheckSuperblock(data, 8 * 1024 * 1024));
    }

    [Fact]
    public void RejectsMissingSignatureTruncationAndUncleanVolume()
    {
        Assert.Contains("not an ext2", Ext2VolumePolicy.CheckSuperblock(new byte[12], 8192));
        byte[] data = Valid(); Put(data, 56, 0);
        Assert.Contains("not an ext2", Ext2VolumePolicy.CheckSuperblock(data, 8 * 1024 * 1024));
        data = Valid(); Put(data, 56, 0x0002EF53);
        Assert.Contains("not clean", Ext2VolumePolicy.CheckSuperblock(data, 8 * 1024 * 1024));
    }
}
