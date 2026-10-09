// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Formats only Zenith's supported profile. Caller must exclude mounted devices.</summary>
internal static class Ext2Formatter
{
    private const uint BlockSize = 1024, BlocksPerGroup = 8192, InodesPerGroup = 1024;
    private const uint TableBlocks = InodesPerGroup * 128 / BlockSize;
    public const ulong MinimumBytes = 1024 * 1024;
    public const ulong MaximumBytes = 512UL * 1024 * 1024;

    /// <summary>Preflight every group and option before any destructive I/O.</summary>
    public static string? Check(IBlockDevice device, string label)
    {
        if ((device.BlockSize != 512 && device.BlockSize != 1024)
            || device.BlockCount > ulong.MaxValue / device.BlockSize)
        {
            return "unsupported device geometry (requires 512 or 1024-byte sectors)";
        }
        ulong bytes = device.BlockCount * device.BlockSize;
        if (bytes < MinimumBytes || bytes > MaximumBytes)
        {
            return "formatter supports partitions from 1 to 512 MiB";
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
        uint blocks = (uint)(bytes / BlockSize);
        uint groups = (blocks - 1 + BlocksPerGroup - 1) / BlocksPerGroup;
        uint descriptors = (groups * 32 + BlockSize - 1) / BlockSize;
        for (uint group = 0; group < groups; group++)
        {
            uint count = Math.Min(BlocksPerGroup, blocks - (1 + group * BlocksPerGroup));
            uint used = 1 + descriptors + 2 + TableBlocks + (group == 0 ? 2u : 0u);
            if (count <= used)
            {
                return "last block group is too small for metadata and free data";
            }
        }
        return null;
    }

    public static void Format(IBlockDevice device, string label = "")
    {
        if (Check(device, label) is string error)
        {
            throw new IOException(error);
        }
        uint blocks = (uint)(device.BlockCount * device.BlockSize / BlockSize);
        uint groups = (blocks - 1 + BlocksPerGroup - 1) / BlocksPerGroup;
        uint descriptorBlocks = (groups * 32 + BlockSize - 1) / BlockSize;
        uint metadata = 1 + descriptorBlocks + 2 + TableBlocks;
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] descriptors = new byte[descriptorBlocks * BlockSize];
        uint freeBlocks = 0;
        for (uint group = 0; group < groups; group++)
        {
            uint start = 1 + group * BlocksPerGroup;
            uint count = Math.Min(BlocksPerGroup, blocks - start);
            uint used = metadata + (group == 0 ? 2u : 0u);
            int offset = (int)(group * 32);
            Put32(descriptors, offset, start + 1 + descriptorBlocks);
            Put32(descriptors, offset + 4, start + 2 + descriptorBlocks);
            Put32(descriptors, offset + 8, start + 3 + descriptorBlocks);
            Put16(descriptors, offset + 12, (ushort)(count - used));
            Put16(descriptors, offset + 14, (ushort)(InodesPerGroup - (group == 0 ? 11u : 0u)));
            Put16(descriptors, offset + 16, (ushort)(group == 0 ? 2 : 0));
            freeBlocks += count - used;
        }
        byte[] superblock = new byte[BlockSize];
        Put32(superblock, 0, groups * InodesPerGroup);
        Put32(superblock, 4, blocks);
        Put32(superblock, 12, freeBlocks);
        Put32(superblock, 16, groups * InodesPerGroup - 11);
        Put32(superblock, 20, 1);
        Put32(superblock, 32, BlocksPerGroup);
        Put32(superblock, 36, BlocksPerGroup);
        Put32(superblock, 40, InodesPerGroup);
        Put32(superblock, 48, now);
        Put16(superblock, 56, 0xEF53);
        Put16(superblock, 58, 1);
        Put16(superblock, 60, 1);
        Put32(superblock, 64, now);
        Put32(superblock, 76, 1);
        Put32(superblock, 84, 11);
        Put16(superblock, 88, 128);
        Put32(superblock, 96, 2); // filetype only: backups required in EVERY group
        Encoding.ASCII.GetBytes(label).CopyTo(superblock.AsSpan(120));

        // Invalidate the old primary first so incomplete metadata is not mountable.
        Write(device, 1, new byte[BlockSize]);
        device.Flush();
        byte[] zero = new byte[BlockSize];
        for (uint group = 0; group < groups; group++)
        {
            uint start = 1 + group * BlocksPerGroup;
            uint count = Math.Min(BlocksPerGroup, blocks - start);
            uint bitmap = start + 1 + descriptorBlocks;
            byte[] blockMap = new byte[BlockSize];
            byte[] inodeMap = new byte[BlockSize];
            uint used = metadata + (group == 0 ? 2u : 0u);
            for (uint bit = 0; bit < BlocksPerGroup; bit++)
            {
                if (bit < used || bit >= count)
                {
                    SetBit(blockMap, bit);
                }
                if (bit >= InodesPerGroup || (group == 0 && bit < 11))
                {
                    SetBit(inodeMap, bit);
                }
            }
            Write(device, bitmap, blockMap);
            Write(device, bitmap + 1, inodeMap);
            for (uint block = 0; block < TableBlocks; block++)
            {
                Write(device, bitmap + 2 + block, zero);
            }
        }
        uint table = 1 + 3 + descriptorBlocks;
        uint rootBlock = 1 + metadata;
        WriteDirectoryInode(device, table, 2, 0x41ed, 3, rootBlock, now);
        WriteDirectoryInode(device, table, 11, 0x41c0, 2, rootBlock + 1, now);
        byte[] root = new byte[BlockSize];
        Entry(root, 0, 2, ".", 12);
        Entry(root, 12, 2, "..", 12);
        Entry(root, 24, 11, "lost+found", 1000);
        Write(device, rootBlock, root);
        byte[] lost = new byte[BlockSize];
        Entry(lost, 0, 11, ".", 12);
        Entry(lost, 12, 2, "..", 1012);
        Write(device, rootBlock + 1, lost);

        // Flush complete metadata before publishing backups, then the primary last.
        device.Flush();
        for (uint group = 0; group < groups; group++)
        {
            uint start = 1 + group * BlocksPerGroup;
            Write(device, start + 1, descriptors);
            if (group != 0)
            {
                Put16(superblock, 90, (ushort)group);
                Write(device, start, superblock);
            }
        }
        device.Flush();
        Put16(superblock, 90, 0);
        Write(device, 1, superblock);
        device.Flush();
    }

    private static void WriteDirectoryInode(IBlockDevice device, uint table, uint inode, ushort mode, ushort links, uint block, uint now)
    {
        uint tableBlock = table + (inode - 1) / 8;
        byte[] data = new byte[BlockSize];
        device.ReadBlock(tableBlock * (BlockSize / device.BlockSize), BlockSize / device.BlockSize, data);
        int offset = (int)((inode - 1) % 8 * 128);
        Put16(data, offset, mode);
        Put32(data, offset + 4, BlockSize);
        Put32(data, offset + 8, now);
        Put32(data, offset + 12, now);
        Put32(data, offset + 16, now);
        Put16(data, offset + 26, links);
        Put32(data, offset + 28, 2);
        Put32(data, offset + 40, block);
        Write(device, tableBlock, data);
    }

    private static void Entry(byte[] data, int offset, uint inode, string name, ushort length)
    {
        Put32(data, offset, inode);
        Put16(data, offset + 4, length);
        data[offset + 6] = (byte)name.Length;
        data[offset + 7] = 2;
        Encoding.ASCII.GetBytes(name).CopyTo(data.AsSpan(offset + 8));
    }

    private static void Write(IBlockDevice device, uint block, ReadOnlySpan<byte> data)
    {
        device.WriteBlock(block * (BlockSize / device.BlockSize), (ulong)data.Length / device.BlockSize, data);
    }
    private static void SetBit(byte[] bitmap, uint bit) => bitmap[bit / 8] |= (byte)(1 << (int)(bit % 8));
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
}
