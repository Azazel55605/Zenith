// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;

namespace Zenith.Core.Storage.Ext2;

/// <summary>
/// The block-group layout of a supported ext2 volume, shared by the formatter, the mount policy and
/// the checker. Each group holds, in order: a superblock and descriptor table copy (every group, or
/// with sparse_super only groups 0, 1 and powers of 3, 5 and 7) followed by the reserved descriptor
/// blocks (resize_inode), the block bitmap, the inode bitmap and the inode table; the rest is data. Pure.
/// </summary>
internal readonly record struct Ext2Geometry(uint BlockSize, uint Blocks, uint BlocksPerGroup, uint InodesPerGroup, bool Sparse, uint ReservedGdt = 0)
{
    public const uint InodeSize = 128;
    /// <summary>The compact profile (1 KiB blocks, a copy in every group, what releases before 0.0.52
    /// formatted up to 512 MiB) only for small volumes: its descriptor table cannot grow, so anything
    /// that may be enlarged later gets the 4 KiB profile with reserved room.</summary>
    public const ulong CompactMaximumBytes = 64UL * 1024 * 1024;
    public const ulong MaximumBytes = 2UL * 1024 * 1024 * 1024 * 1024;
    /// <summary>Above this the 4 KiB profile spreads fewer inodes per group (one per 32 KiB up to it).</summary>
    public const ulong TargetInodes = 4UL * 1024 * 1024;

    public uint FirstDataBlock => BlockSize == 1024 ? 1u : 0u;
    public uint Groups => (Blocks - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup;
    public uint DescriptorBlocks => (Groups * 32 + BlockSize - 1) / BlockSize;
    public uint TableBlocks => InodesPerGroup * InodeSize / BlockSize;
    public uint Inodes => Groups * InodesPerGroup;
    public uint LogBlockSize => BlockSize == 1024 ? 0u : BlockSize == 2048 ? 1u : 2u;
    public uint RoCompat => Sparse ? Ext2SuperblockLayout.RoCompatSparseSuper | Ext2SuperblockLayout.RoCompatLargeFile : 0u;
    public uint Compat => ReservedGdt > 0 ? Ext2SuperblockLayout.CompatResizeInode : 0u;
    /// <summary>Descriptor blocks a 2 TiB volume of this block size needs: the growth limit.</summary>
    public uint MaximumDescriptorBlocks => (uint)(((MaximumBytes / BlockSize + BlocksPerGroup - 1) / BlocksPerGroup * 32 + BlockSize - 1) / BlockSize);
    /// <summary>Group 0 holds the root and lost+found directory blocks, and the resize inode's table.</summary>
    public uint GroupZeroExtra => 2 + (ReservedGdt > 0 ? 1u : 0u);

    public static bool HasBackup(uint group, bool sparse)
    {
        if (!sparse || group <= 1) { return true; }
        foreach (uint factor in new uint[] { 3, 5, 7 })
        {
            uint power = factor;
            while (power < group) { power *= factor; }
            if (power == group) { return true; }
        }
        return false;
    }

    public bool HasBackup(uint group) => HasBackup(group, Sparse);
    public uint GroupStart(uint group) => FirstDataBlock + group * BlocksPerGroup;
    public uint GroupBlocks(uint group) => Math.Min(BlocksPerGroup, Blocks - GroupStart(group));
    /// <summary>Superblock, descriptor copy and reserved blocks (when present), both bitmaps and the inode table.</summary>
    public uint GroupMetadata(uint group) => (HasBackup(group) ? 1 + DescriptorBlocks + ReservedGdt : 0) + 2 + TableBlocks;
    public uint BlockBitmap(uint group) => GroupStart(group) + (HasBackup(group) ? 1 + DescriptorBlocks + ReservedGdt : 0);

    /// <summary>The layout this release formats for a device of <paramref name="bytes"/>, or null when
    /// none fits (too small or too large, or a group too small for its metadata).</summary>
    public static Ext2Geometry? For(ulong bytes)
    {
        if (bytes < 1024 * 1024 || bytes > MaximumBytes) { return null; }
        if (bytes <= CompactMaximumBytes)
        {
            var compact = new Ext2Geometry(1024, (uint)(bytes / 1024), 8192, 1024, false);
            return compact.GroupsFit() ? compact : null;
        }
        uint blocks = (uint)(bytes / 4096);
        // One inode per 32 KiB, fewer once that passes about four million: a whole-volume check keeps
        // two bits per block and two bytes per inode in memory.
        uint groups = (blocks + 32767) / 32768;
        uint perGroup = (uint)Math.Clamp(TargetInodes / groups, 256UL, 4096UL) / 32 * 32;
        var large = new Ext2Geometry(4096, blocks, 32768, perGroup, true);
        // Room for the descriptor table to grow to 2 TiB (resize_inode), at most what one
        // double-indirect table can list (mke2fs reserves for 1024 times the size, same bound).
        large = large with { ReservedGdt = Math.Min(large.MaximumDescriptorBlocks - large.DescriptorBlocks, large.BlockSize / 4) };
        // A last group too small for its own metadata and some data is left off, as mke2fs does.
        uint last = large.Groups - 1;
        if (large.GroupBlocks(last) < large.GroupMetadata(last) + 64)
        {
            if (last == 0) { return null; }
            large = large with { Blocks = large.GroupStart(last) };
            large = large with { ReservedGdt = Math.Min(large.MaximumDescriptorBlocks - large.DescriptorBlocks, large.BlockSize / 4) };
        }
        return large.GroupsFit() ? large : null;
    }

    /// <summary>Every group can hold its metadata (group 0 also the root and lost+found blocks) and data.</summary>
    public bool GroupsFit()
    {
        if (ReservedGdt > BlockSize / 4 || DescriptorBlocks + ReservedGdt > MaximumDescriptorBlocks + 1) { return false; }
        if (Blocks <= FirstDataBlock || Groups == 0 || TableBlocks == 0 || InodesPerGroup > BlockSize * 8 || BlocksPerGroup > BlockSize * 8)
        {
            return false;
        }
        for (uint group = 0; group < Groups; group++)
        {
            if (GroupBlocks(group) <= GroupMetadata(group) + (group == 0 ? GroupZeroExtra : 0u)) { return false; }
        }
        return true;
    }
}
