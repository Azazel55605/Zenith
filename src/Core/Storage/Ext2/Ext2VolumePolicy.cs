using System;
using System.Buffers.Binary;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Conservative feature gate for the experimental Cosmos ext2 driver.
/// This checks the supported profile, not filesystem consistency (fsck is separate).</summary>
internal static class Ext2VolumePolicy
{
    public static string? Check(IBlockDevice device)
    {
        if ((device.BlockSize != 512 && device.BlockSize != 1024) || device.BlockCount > ulong.MaxValue / device.BlockSize
            || device.BlockCount < 2048 / device.BlockSize)
        {
            return "unsupported ext2 device geometry";
        }
        byte[] data = new byte[1024];
        device.ReadBlock(1024 / device.BlockSize, 1024 / device.BlockSize, data);
        return CheckSuperblock(data, device.BlockCount * device.BlockSize);
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));

    internal static string? CheckSuperblock(ReadOnlySpan<byte> data, ulong deviceBytes)
    {
        if (data.Length < 1024 || BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(56)) != 0xEF53)
        {
            return "not an ext2 filesystem";
        }
        // Profile deliberately excludes journal, extents, checksums and unknown features.
        if (Read(data, 76) != 1 || Read(data, 24) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(88)) != 128
            || Read(data, 92) != 0 || Read(data, 96) != 2 || Read(data, 100) != 0)
        {
            return "unsupported ext2 profile (requires revision 1, 1 KiB blocks, 128-byte inodes, filetype only)";
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(58)) != 1)
        {
            return "ext2 volume is not clean; check it with host e2fsck before mounting";
        }
        uint blocks = Read(data, 4), perGroup = Read(data, 32), inodes = Read(data, 0), inodesPerGroup = Read(data, 40);
        if (blocks < 16 || (ulong)blocks * 1024 > deviceBytes || Read(data, 20) != 1
            || perGroup == 0 || perGroup > 8192 || inodesPerGroup == 0 || inodesPerGroup > 8192
            || inodes < 11 || Read(data, 12) > blocks || Read(data, 16) > inodes
            || Read(data, 28) != 0 || Read(data, 36) != perGroup)
        {
            return "invalid ext2 geometry";
        }
        return null;
    }
}
