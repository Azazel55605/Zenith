// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Buffers.Binary;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>
/// Grows an unmounted, clean ext2 volume to fill its (already enlarged) device. New groups are
/// added at the end and the last group is extended; nothing that exists moves. A larger descriptor
/// table takes the reserved descriptor blocks that follow every copy (resize_inode), so a volume
/// formatted by Zenith's 4 KiB profile can grow to 2 TiB; the 1 KiB profile grows only within its
/// last descriptor block. Not crash-atomic: the primary superblock is written last, and the caller
/// checks the volume afterwards. Shrinking is not supported.
/// </summary>
internal static class Ext2Resizer
{
    /// <summary>The geometry the volume would have after growing, or the reason it cannot grow.</summary>
    public static string? Plan(IBlockDevice device, out Ext2Geometry before, out Ext2Geometry after)
    {
        before = after = default;
        if (Ext2VolumePolicy.Check(device) is string profile) { return profile; }
        if (!Ext2Superblock.TryCreate(device, out var sb)) { return "invalid ext2 metadata"; }
        byte[] raw = Ext2VolumePolicy.ReadPrimary(device);
        uint reserved = (sb.FeatureCompat & Ext2SuperblockLayout.CompatResizeInode) != 0
            ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(Ext2SuperblockLayout.ReservedGdtBlocksOffset)) : 0u;
        before = new Ext2Geometry(sb.BlockSize, sb.BlocksCount, sb.BlocksPerGroup, sb.InodesPerGroup,
            (sb.FeatureRoCompat & Ext2SuperblockLayout.RoCompatSparseSuper) != 0, reserved);
        if (sb.InodeSize != Ext2Geometry.InodeSize || sb.InodesCount != before.Inodes || sb.GroupsCount != before.Groups || !before.GroupsFit())
        {
            return "unsupported ext2 layout for growing";
        }
        ulong bytes = Math.Min(device.BlockCount * device.BlockSize, Ext2Geometry.MaximumBytes);
        uint blocks = (uint)Math.Min(bytes / sb.BlockSize, uint.MaxValue);
        var grown = before with { Blocks = blocks };
        uint last = grown.Groups - 1;
        if (last >= before.Groups && grown.GroupBlocks(last) < GroupMinimum(grown, last))
        {
            grown = grown with { Blocks = grown.GroupStart(last) };
        }
        if (grown.Blocks <= before.Blocks) { return "the partition has no room beyond the volume; enlarge the partition first"; }
        uint growth = grown.DescriptorBlocks - before.DescriptorBlocks;
        if (growth > before.ReservedGdt)
        {
            ulong limit = (ulong)(before.DescriptorBlocks + before.ReservedGdt) * before.BlockSize / 32 * before.BlocksPerGroup * before.BlockSize;
            return "the descriptor table has room for " + (limit >> 20) + " MiB; this volume cannot grow further in place";
        }
        grown = grown with { ReservedGdt = before.ReservedGdt - growth };
        if (!grown.GroupsFit()) { return "the grown layout does not fit"; }
        after = grown;
        return null;
    }

    private static uint GroupMinimum(Ext2Geometry g, uint group) => g.GroupMetadata(group) + 64;

    public static void Grow(IBlockDevice device)
    {
        if (Plan(device, out var before, out var after) is string error) { throw new IOException(error); }
        if (!Ext2Superblock.TryCreate(device, out var sb)) { throw new IOException("invalid ext2 metadata"); }
        uint bs = after.BlockSize, oldGroups = before.Groups, newGroups = after.Groups;
        var g = after;
        // Descriptors for every group: the existing ones as stored, the new ones as built here.
        byte[] descriptors = new byte[g.DescriptorBlocks * bs];
        ReadBlocks(device, bs, g.FirstDataBlock + 1, before.DescriptorBlocks, descriptors.AsSpan(0, (int)(before.DescriptorBlocks * bs)));
        Array.Clear(descriptors, (int)(oldGroups * 32), descriptors.Length - (int)(oldGroups * 32));
        byte[] blockMap = new byte[bs], inodeMap = new byte[bs];
        uint run = Math.Min(g.TableBlocks, 256 * 1024 / bs);
        byte[] zero = new byte[Math.Max(run, 1) * bs];

        // 1. New groups: bitmaps, an empty inode table and zeroed reserved blocks; nothing refers to them yet.
        for (uint group = oldGroups; group < newGroups; group++)
        {
            uint start = g.GroupStart(group), count = g.GroupBlocks(group), used = g.GroupMetadata(group), bitmap = g.BlockBitmap(group);
            Array.Clear(blockMap); Array.Clear(inodeMap);
            for (uint bit = 0; bit < bs * 8; bit++)
            {
                if (bit < used || bit >= count) { SetBit(blockMap, bit); }
                if (bit >= g.InodesPerGroup) { SetBit(inodeMap, bit); }
            }
            WriteBlocks(device, bs, bitmap, blockMap);
            WriteBlocks(device, bs, bitmap + 1, inodeMap);
            for (uint block = 0; block < g.TableBlocks; block += run)
            {
                uint n = Math.Min(run, g.TableBlocks - block);
                WriteBlocks(device, bs, bitmap + 2 + block, zero.AsSpan(0, (int)(n * bs)));
            }
            if (g.HasBackup(group))
            {
                for (uint i = 0; i < g.ReservedGdt; i++) { WriteBlocks(device, bs, start + 1 + g.DescriptorBlocks + i, new byte[bs]); }
            }
            int offset = (int)(group * 32);
            Put32(descriptors, offset, bitmap);
            Put32(descriptors, offset + 4, bitmap + 1);
            Put32(descriptors, offset + 8, bitmap + 2);
            Put16(descriptors, offset + 12, (ushort)(count - used));
            Put16(descriptors, offset + 14, (ushort)g.InodesPerGroup);
            Put16(descriptors, offset + 16, 0);
        }
        device.Flush();

        // 2. The last existing group takes the blocks that were past the old end.
        uint lastOld = oldGroups - 1, oldCount = before.GroupBlocks(lastOld), newCount = g.GroupBlocks(lastOld);
        if (newCount > oldCount)
        {
            uint bitmap = BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan((int)(lastOld * 32)));
            ReadBlocks(device, bs, bitmap, 1, blockMap);
            for (uint bit = oldCount; bit < newCount; bit++) { blockMap[bit / 8] &= (byte)~(1 << (int)(bit % 8)); }
            WriteBlocks(device, bs, bitmap, blockMap);
            ushort free = BinaryPrimitives.ReadUInt16LittleEndian(descriptors.AsSpan((int)(lastOld * 32 + 12)));
            Put16(descriptors, (int)(lastOld * 32 + 12), (ushort)(free + newCount - oldCount));
        }

        // 3. The resize inode lists the reserved blocks that remain, for every backup group now present.
        if (before.ReservedGdt > 0) { RebuildResizeInode(device, sb, before, g, descriptors); }

        // 4. Every descriptor table copy, then every superblock copy, the primary last.
        uint freeBlocks = 0, freeInodes = 0;
        for (uint group = 0; group < newGroups; group++)
        {
            freeBlocks += BinaryPrimitives.ReadUInt16LittleEndian(descriptors.AsSpan((int)(group * 32 + 12)));
            freeInodes += BinaryPrimitives.ReadUInt16LittleEndian(descriptors.AsSpan((int)(group * 32 + 14)));
        }
        for (uint group = 0; group < newGroups; group++)
        {
            if (g.HasBackup(group)) { WriteBlocks(device, bs, g.GroupStart(group) + 1, descriptors); }
        }
        device.Flush();
        byte[] superblock = Ext2VolumePolicy.ReadPrimary(device);
        Put32(superblock, 0, g.Inodes);
        Put32(superblock, 4, g.Blocks);
        Put32(superblock, 12, freeBlocks);
        Put32(superblock, 16, freeInodes);
        Put32(superblock, 92, BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(92)) & ~Ext2SuperblockLayout.CompatResizeInode | g.Compat);
        Put16(superblock, Ext2SuperblockLayout.ReservedGdtBlocksOffset, (ushort)g.ReservedGdt);
        for (uint group = 1; group < newGroups; group++)
        {
            if (!g.HasBackup(group)) { continue; }
            Put16(superblock, 90, (ushort)group);
            WriteBytes(device, (ulong)g.GroupStart(group) * bs, superblock);
        }
        device.Flush();
        Put16(superblock, 90, 0);
        WriteBytes(device, 1024, superblock);
        device.Flush();
    }

    /// <summary>The same structure the formatter writes, for the grown layout; the reserved blocks the
    /// table grew into leave it. With none left the feature and inode 7 are cleared.</summary>
    private static void RebuildResizeInode(IBlockDevice device, Ext2Superblock sb, Ext2Geometry before, Ext2Geometry g, byte[] descriptors)
    {
        uint bs = g.BlockSize;
        var inode = sb.ReadInode(Ext2SuperblockLayout.ResizeInode, "");
        uint dind = inode.Block[13];
        if (dind == 0 || dind >= before.Blocks) { throw new IOException("resize inode has no table"); }
        uint table = BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan(8));
        if (g.ReservedGdt == 0)
        {
            // Release the table block (group of the block) and clear inode 7.
            uint group = (dind - g.FirstDataBlock) / g.BlocksPerGroup, index = (dind - g.FirstDataBlock) % g.BlocksPerGroup;
            uint bitmap = BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan((int)(group * 32)));
            byte[] map = new byte[bs];
            ReadBlocks(device, bs, bitmap, 1, map);
            map[index / 8] &= (byte)~(1 << (int)(index % 8));
            WriteBlocks(device, bs, bitmap, map);
            ushort free = BinaryPrimitives.ReadUInt16LittleEndian(descriptors.AsSpan((int)(group * 32 + 12)));
            Put16(descriptors, (int)(group * 32 + 12), (ushort)(free + 1));
            uint perTable = bs / Ext2Geometry.InodeSize, tableBlock = table + (Ext2SuperblockLayout.ResizeInode - 1) / perTable;
            byte[] data = new byte[bs];
            ReadBlocks(device, bs, tableBlock, 1, data);
            data.AsSpan((int)((Ext2SuperblockLayout.ResizeInode - 1) % perTable * Ext2Geometry.InodeSize), (int)Ext2Geometry.InodeSize).Clear();
            WriteBlocks(device, bs, tableBlock, data);
            return;
        }
        Ext2Formatter.WriteResizeInode(device, g, table, dind, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static void ReadBlocks(IBlockDevice device, uint bs, uint block, uint count, Span<byte> data)
        => device.ReadBlock((ulong)block * (bs / device.BlockSize), (ulong)count * (bs / device.BlockSize), data);
    private static void WriteBlocks(IBlockDevice device, uint bs, uint block, ReadOnlySpan<byte> data)
        => device.WriteBlock((ulong)block * (bs / device.BlockSize), (ulong)data.Length / device.BlockSize, data);
    private static void WriteBytes(IBlockDevice device, ulong offset, ReadOnlySpan<byte> data)
    {
        ulong sector = device.BlockSize, first = offset / sector, last = (offset + (ulong)data.Length + sector - 1) / sector;
        byte[] buffer = new byte[(last - first) * sector];
        device.ReadBlock(first, last - first, buffer);
        data.CopyTo(buffer.AsSpan((int)(offset - first * sector)));
        device.WriteBlock(first, last - first, buffer);
    }
    private static void SetBit(byte[] bitmap, uint bit) => bitmap[bit / 8] |= (byte)(1 << (int)(bit % 8));
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
}
