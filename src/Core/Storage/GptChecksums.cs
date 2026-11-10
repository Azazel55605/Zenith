using System;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage;

/// <summary>
/// Completes a GPT written by Cosmos' <c>Gpt</c> class, which (by design, for test images)
/// leaves the header and entry-array CRC32s at zero and writes no backup header. UEFI firmware
/// rejects such a disk, so the installer runs this afterwards: it fills in both CRCs and writes
/// the backup entry array and header at the end of the disk, as the UEFI spec (5.3) requires.
/// </summary>
internal static class GptChecksums
{
    private const int SectorSize = 512;
    private const int EntryArraySectors = 32;   // 128 entries x 128 bytes

    private static readonly uint[] s_table = CreateTable();

    public static void Complete(IBlockDevice disk)
    {
        if (disk.BlockSize != SectorSize)
        {
            throw new NotSupportedException("GPT finalization assumes 512-byte sectors");
        }

        byte[] header = new byte[SectorSize];
        disk.ReadBlock(1, 1, header);
        if (header[0] != (byte)'E' || header[1] != (byte)'F' || header[2] != (byte)'I')
        {
            throw new InvalidOperationException("no GPT header at LBA 1");
        }

        int headerSize = BitConverter.ToInt32(header, 12);
        ulong backupLba = disk.BlockCount - 1;
        ulong backupEntriesLba = backupLba - EntryArraySectors;

        byte[] entries = new byte[EntryArraySectors * SectorSize];
        disk.ReadBlock(2, EntryArraySectors, entries);
        uint entriesCrc = Crc32(entries, entries.Length);

        // Primary header: the backup location and last usable sector at the disk's real end (it may
        // have been enlarged since the table was written), the entry-array CRC, then the header CRC
        // computed with its own field zeroed.
        BitConverter.TryWriteBytes(header.AsSpan(32, 8), backupLba);
        BitConverter.TryWriteBytes(header.AsSpan(48, 8), backupEntriesLba - 1);
        BitConverter.TryWriteBytes(header.AsSpan(88, 4), entriesCrc);
        StampHeaderCrc(header, headerSize);
        disk.WriteBlock(1, 1, header);

        // Backup: same entries, header with the LBA fields swapped.
        byte[] backup = (byte[])header.Clone();
        BitConverter.TryWriteBytes(backup.AsSpan(24, 8), backupLba);   // MyLBA
        BitConverter.TryWriteBytes(backup.AsSpan(32, 8), 1UL);         // AlternateLBA
        BitConverter.TryWriteBytes(backup.AsSpan(72, 8), backupEntriesLba);
        StampHeaderCrc(backup, headerSize);
        disk.WriteBlock(backupEntriesLba, EntryArraySectors, entries);
        disk.WriteBlock(backupLba, 1, backup);
        disk.Flush();
    }

    /// <summary>Whether the primary and backup GPT are both intact (header and entry CRCs) and list
    /// the same partitions. Reads 66 sectors; false on any defect or unreadable copy.</summary>
    public static bool CopiesMatch(IBlockDevice disk)
    {
        try
        {
            if (disk.BlockSize != SectorSize || disk.BlockCount < 2 * (EntryArraySectors + 2)) { return false; }
            byte[] primary = new byte[SectorSize];
            disk.ReadBlock(1, 1, primary);
            if (!ValidHeader(primary, 1)) { return false; }
            ulong alternate = BitConverter.ToUInt64(primary, 32);
            if (alternate >= disk.BlockCount || alternate <= 1) { return false; }
            byte[] backup = new byte[SectorSize];
            disk.ReadBlock(alternate, 1, backup);
            if (!ValidHeader(backup, alternate)) { return false; }
            uint crc = BitConverter.ToUInt32(primary, 88);
            if (BitConverter.ToUInt32(backup, 88) != crc || !primary.AsSpan(56, 16).SequenceEqual(backup.AsSpan(56, 16))) { return false; }
            return EntriesCrc(disk, BitConverter.ToUInt64(primary, 72), primary) == crc
                && EntriesCrc(disk, BitConverter.ToUInt64(backup, 72), backup) == crc;
        }
        catch { return false; }
    }

    private static bool ValidHeader(byte[] header, ulong lba)
    {
        if (header[0] != (byte)'E' || header[1] != (byte)'F' || header[2] != (byte)'I' || header[3] != (byte)' ') { return false; }
        int size = BitConverter.ToInt32(header, 12);
        if (size < 92 || size > SectorSize || BitConverter.ToUInt64(header, 24) != lba) { return false; }
        uint stored = BitConverter.ToUInt32(header, 16);
        byte[] copy = (byte[])header.Clone();
        BitConverter.TryWriteBytes(copy.AsSpan(16, 4), 0u);
        return Crc32(copy, size) == stored;
    }

    private static uint EntriesCrc(IBlockDevice disk, ulong lba, byte[] header)
    {
        uint count = BitConverter.ToUInt32(header, 80), size = BitConverter.ToUInt32(header, 84);
        if (size < 128 || size > 512 || count == 0 || (ulong)count * size > 128UL * 1024) { return 0; }
        int bytes = (int)(count * size), sectors = (bytes + SectorSize - 1) / SectorSize;
        if (lba + (ulong)sectors > disk.BlockCount) { return 0; }
        byte[] entries = new byte[sectors * SectorSize];
        disk.ReadBlock(lba, (ulong)sectors, entries);
        return Crc32(entries, bytes);
    }

    private static void StampHeaderCrc(byte[] header, int headerSize)
    {
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), 0u);
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), Crc32(header, headerSize));
    }

    internal static uint Crc32(byte[] data, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < length; i++)
        {
            crc = s_table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
