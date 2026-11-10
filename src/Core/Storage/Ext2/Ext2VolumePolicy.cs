using System;
using System.Buffers.Binary;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Conservative feature gate for the experimental Cosmos ext2 driver.
/// This checks the supported profile, not filesystem consistency (fsck is separate).</summary>
internal static class Ext2VolumePolicy
{
    public static string? Check(IBlockDevice device, bool allowUnclean = false)
    {
        if ((device.BlockSize != 512 && device.BlockSize != 1024 && device.BlockSize != 4096)
            || device.BlockCount > ulong.MaxValue / device.BlockSize || device.BlockCount < 4096 / device.BlockSize)
        {
            return "unsupported ext2 device geometry";
        }
        // The primary superblock: bytes 1024-2047, whatever the sector size.
        ulong sector = device.BlockSize, first = 1024 / sector, count = Math.Max(1, 2048 / sector - first);
        byte[] sectors = new byte[count * sector];
        device.ReadBlock(first, count, sectors);
        ReadOnlySpan<byte> data = sectors.AsSpan((int)(1024 - first * sector), 1024);
        string? result = CheckSuperblock(data, device.BlockCount * device.BlockSize, allowUnclean);
        if (result is null && (1024u << (int)Read(data, 24)) < device.BlockSize) { return "ext2 block smaller than the device sector"; }
        return result;
    }

    internal static bool CanMount(IBlockDevice device, out string report)
    {
        report = Check(device) ?? "";
        if (report == "") { return true; }
        if (Check(device, allowUnclean: true) is string invalid) { report = invalid; return false; }
        // A crash leaves the mounted-state bit unset even after a complete flush.
        // Never clear it or mount writable merely because the geometry is supported.
        if (!Ext2Checker.Check(device, out report, allowUnclean: true)) { return false; }
        report = "unclean ext2 passed read-only consistency check; no repairs made";
        return true;
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));

    /// <summary>The primary superblock's 1024 bytes, whatever the sector size.</summary>
    internal static byte[] ReadPrimary(IBlockDevice device)
    {
        ulong sector = device.BlockSize, first = 1024 / sector, count = Math.Max(1, 2048 / sector - first);
        byte[] sectors = new byte[count * sector];
        device.ReadBlock(first, count, sectors);
        return sectors.AsSpan((int)(1024 - first * sector), 1024).ToArray();
    }

    internal static string? CheckSuperblock(ReadOnlySpan<byte> data, ulong deviceBytes, bool allowUnclean = false)
    {
        if (data.Length < 1024 || BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(56)) != 0xEF53)
        {
            return "not an ext2 filesystem";
        }
        // Profiles: 1 KiB or 4 KiB blocks, Linux layout revision 1, 128-byte inodes, filetype as the
        // only incompatible feature, at most sparse_super and large_file read-only-compatible ones.
        // Journal, extents, checksums and unknown features are refused.
        uint log = Read(data, 24), roCompat = Read(data, 100);
        if (Read(data, 72) != 0 || Read(data, 76) != 1 || (log != 0 && log != 2) || BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(88)) != 128
            || (Read(data, 92) & ~Ext2SuperblockLayout.CompatResizeInode) != 0 || Read(data, 96) != 2
            || (roCompat & ~(Ext2SuperblockLayout.RoCompatSparseSuper | Ext2SuperblockLayout.RoCompatLargeFile)) != 0)
        {
            return "unsupported ext2 profile (requires Linux layout, revision 1, 1 or 4 KiB blocks, 128-byte inodes, filetype, sparse_super/large_file/resize_inode only)";
        }
        ushort state = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(58));
        if (state != 1 && (!allowUnclean || state != 0))
        {
            return "ext2 volume is not clean; check it with host e2fsck before mounting";
        }
        uint blockSize = 1024u << (int)log, bits = blockSize * 8;
        uint blocks = Read(data, 4), perGroup = Read(data, 32), inodes = Read(data, 0), inodesPerGroup = Read(data, 40);
        if (blocks < 16 || (ulong)blocks * blockSize > deviceBytes || (ulong)blocks * blockSize > Ext2Geometry.MaximumBytes
            || Read(data, 20) != (log == 0 ? 1u : 0u)
            || perGroup == 0 || perGroup > bits || inodesPerGroup == 0 || inodesPerGroup > bits
            || inodesPerGroup * 128 % blockSize != 0
            || inodes < 11 || Read(data, 12) > blocks || Read(data, 16) > inodes
            || Read(data, 28) != log || Read(data, 36) != perGroup)
        {
            return "invalid ext2 geometry";
        }
        return null;
    }
}
