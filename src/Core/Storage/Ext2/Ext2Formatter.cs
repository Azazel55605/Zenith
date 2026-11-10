// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Formats only Zenith's supported profiles (<see cref="Ext2Geometry"/>): 1 KiB blocks up to
/// 512 MiB, 4 KiB blocks with sparse_super and large_file up to 2 TiB. Caller must exclude mounted devices.</summary>
internal static class Ext2Formatter
{
    public const ulong MinimumBytes = 1024 * 1024;
    public const ulong MaximumBytes = Ext2Geometry.MaximumBytes;

    /// <summary>Preflight every group and option before any destructive I/O.</summary>
    public static string? Check(IBlockDevice device, string label) => Check(device, label, out _);

    private static string? Check(IBlockDevice device, string label, out Ext2Geometry geometry)
    {
        geometry = default;
        if ((device.BlockSize != 512 && device.BlockSize != 1024 && device.BlockSize != 4096)
            || device.BlockCount > ulong.MaxValue / device.BlockSize)
        {
            return "unsupported device geometry (requires 512, 1024 or 4096-byte sectors)";
        }
        ulong bytes = device.BlockCount * device.BlockSize;
        if (bytes < MinimumBytes || bytes > MaximumBytes)
        {
            return "formatter supports partitions from 1 MiB to 2 TiB";
        }
        if (label.Length > 16)
        {
            return "label must contain at most 16 printable ASCII characters";
        }
        foreach (char character in label)
        {
            if (character < ' ' || character > '~')
            {
                return "label must contain at most 16 printable ASCII characters";
            }
        }
        if (Ext2Geometry.For(bytes) is not Ext2Geometry chosen || chosen.BlockSize < device.BlockSize)
        {
            return "last block group is too small for metadata and free data";
        }
        geometry = chosen;
        return null;
    }

    public static void Format(IBlockDevice device, string label = "")
    {
        if (Check(device, label, out var g) is string error)
        {
            throw new IOException(error);
        }
        uint bs = g.BlockSize, groups = g.Groups, inodesPerBlock = bs / Ext2Geometry.InodeSize;
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] descriptors = new byte[g.DescriptorBlocks * bs];
        uint freeBlocks = 0;
        for (uint group = 0; group < groups; group++)
        {
            uint count = g.GroupBlocks(group), used = g.GroupMetadata(group) + (group == 0 ? 2u : 0u);
            uint bitmap = g.BlockBitmap(group);
            int offset = (int)(group * 32);
            Put32(descriptors, offset, bitmap);
            Put32(descriptors, offset + 4, bitmap + 1);
            Put32(descriptors, offset + 8, bitmap + 2);
            Put16(descriptors, offset + 12, (ushort)(count - used));
            Put16(descriptors, offset + 14, (ushort)(g.InodesPerGroup - (group == 0 ? 11u : 0u)));
            Put16(descriptors, offset + 16, (ushort)(group == 0 ? 2 : 0));
            freeBlocks += count - used;
        }
        byte[] superblock = new byte[1024];
        Put32(superblock, 0, g.Inodes);
        Put32(superblock, 4, g.Blocks);
        Put32(superblock, 12, freeBlocks);
        Put32(superblock, 16, g.Inodes - 11);
        Put32(superblock, 20, g.FirstDataBlock);
        Put32(superblock, 24, g.LogBlockSize);
        Put32(superblock, 28, g.LogBlockSize);
        Put32(superblock, 32, g.BlocksPerGroup);
        Put32(superblock, 36, g.BlocksPerGroup);
        Put32(superblock, 40, g.InodesPerGroup);
        Put32(superblock, 48, now);
        Put16(superblock, 56, 0xEF53);
        Put16(superblock, 58, 1);
        Put16(superblock, 60, 1);
        Put32(superblock, 64, now);
        Put32(superblock, 76, 1);
        Put32(superblock, 84, 11);
        Put16(superblock, 88, (ushort)Ext2Geometry.InodeSize);
        Put32(superblock, 96, 2); // filetype
        Put32(superblock, 100, g.RoCompat);
        Encoding.ASCII.GetBytes(label).CopyTo(superblock.AsSpan(120));

        // Invalidate the old primary first so incomplete metadata is not mountable.
        WriteBytes(device, 1024, new byte[1024]);
        device.Flush();
        // Inode tables in runs of up to 256 KiB of zeros.
        uint run = Math.Min(g.TableBlocks, 256 * 1024 / bs);
        byte[] zero = new byte[run * bs];
        byte[] blockMap = new byte[bs], inodeMap = new byte[bs];
        for (uint group = 0; group < groups; group++)
        {
            uint count = g.GroupBlocks(group), used = g.GroupMetadata(group) + (group == 0 ? 2u : 0u);
            uint bitmap = g.BlockBitmap(group);
            Array.Clear(blockMap); Array.Clear(inodeMap);
            for (uint bit = 0; bit < bs * 8; bit++)
            {
                if (bit < used || bit >= count)
                {
                    SetBit(blockMap, bit);
                }
                if (bit >= g.InodesPerGroup || (group == 0 && bit < 11))
                {
                    SetBit(inodeMap, bit);
                }
            }
            Write(device, bs, bitmap, blockMap);
            Write(device, bs, bitmap + 1, inodeMap);
            for (uint block = 0; block < g.TableBlocks; block += run)
            {
                uint n = Math.Min(run, g.TableBlocks - block);
                Write(device, bs, bitmap + 2 + block, zero.AsSpan(0, (int)(n * bs)));
            }
        }
        uint table = g.BlockBitmap(0) + 2;
        uint rootBlock = g.GroupStart(0) + g.GroupMetadata(0);
        WriteDirectoryInode(device, bs, table, 2, 0x41ed, 3, rootBlock, now);
        WriteDirectoryInode(device, bs, table, 11, 0x41c0, 2, rootBlock + 1, now);
        byte[] root = new byte[bs];
        Entry(root, 0, 2, ".", 12);
        Entry(root, 12, 2, "..", 12);
        Entry(root, 24, 11, "lost+found", (ushort)(bs - 24));
        Write(device, bs, rootBlock, root);
        byte[] lost = new byte[bs];
        Entry(lost, 0, 11, ".", 12);
        Entry(lost, 12, 2, "..", (ushort)(bs - 12));
        Write(device, bs, rootBlock + 1, lost);

        // Flush complete metadata before publishing backups, then the primary last.
        device.Flush();
        for (uint group = 0; group < groups; group++)
        {
            if (!g.HasBackup(group))
            {
                continue;
            }
            uint start = g.GroupStart(group);
            Write(device, bs, start + 1, descriptors);
            if (group != 0)
            {
                Put16(superblock, 90, (ushort)group);
                WriteBytes(device, (ulong)start * bs, superblock);
            }
        }
        device.Flush();
        Put16(superblock, 90, 0);
        WriteBytes(device, 1024, superblock);
        device.Flush();
    }

    private static void WriteDirectoryInode(IBlockDevice device, uint bs, uint table, uint inode, ushort mode, ushort links, uint block, uint now)
    {
        uint perBlock = bs / Ext2Geometry.InodeSize;
        uint tableBlock = table + (inode - 1) / perBlock;
        byte[] data = new byte[bs];
        device.ReadBlock((ulong)tableBlock * (bs / device.BlockSize), bs / device.BlockSize, data);
        int offset = (int)((inode - 1) % perBlock * Ext2Geometry.InodeSize);
        Put16(data, offset, mode);
        Put32(data, offset + 4, bs);
        Put32(data, offset + 8, now);
        Put32(data, offset + 12, now);
        Put32(data, offset + 16, now);
        Put16(data, offset + 26, links);
        Put32(data, offset + 28, bs / 512);
        Put32(data, offset + 40, block);
        Write(device, bs, tableBlock, data);
    }

    private static void Entry(byte[] data, int offset, uint inode, string name, ushort length)
    {
        Put32(data, offset, inode);
        Put16(data, offset + 4, length);
        data[offset + 6] = (byte)name.Length;
        data[offset + 7] = 2;
        Encoding.ASCII.GetBytes(name).CopyTo(data.AsSpan(offset + 8));
    }

    private static void Write(IBlockDevice device, uint bs, uint block, ReadOnlySpan<byte> data)
    {
        device.WriteBlock((ulong)block * (bs / device.BlockSize), (ulong)data.Length / device.BlockSize, data);
    }

    /// <summary>A whole-sector write at a byte offset (a superblock at 1024 inside a 4 KiB block).</summary>
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
