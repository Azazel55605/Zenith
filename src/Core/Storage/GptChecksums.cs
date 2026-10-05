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

        // Primary header: entry-array CRC, then the header CRC computed with its own field zeroed.
        BitConverter.TryWriteBytes(header.AsSpan(32, 8), backupLba);
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

    private static void StampHeaderCrc(byte[] header, int headerSize)
    {
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), 0u);
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), Crc32(header, headerSize));
    }

    private static uint Crc32(byte[] data, int length)
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
