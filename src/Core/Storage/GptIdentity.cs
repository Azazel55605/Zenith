using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage;

internal sealed record GptIdentity(Guid Type, Guid Id, ulong Start, ulong Count)
{
    public static readonly Guid EspType = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static IReadOnlyList<GptIdentity> Read(IBlockDevice disk)
    {
        if (disk.BlockSize != 512 || disk.BlockCount < 34) { throw new IOException("unsupported GPT identity geometry"); }
        byte[] header = new byte[512]; disk.ReadBlock(1, 1, header);
        if (BitConverter.ToUInt64(header, 0) != 0x5452415020494645UL) { throw new IOException("missing GPT identity header"); }
        uint size = BitConverter.ToUInt32(header, 12), crc = BitConverter.ToUInt32(header, 16);
        if (size < 92 || size > 512 || BitConverter.ToUInt64(header, 24) != 1) { throw new IOException("invalid GPT identity header"); }
        Array.Clear(header, 16, 4);
        if (GptChecksums.Crc32(header, (int)size) != crc) { throw new IOException("GPT header checksum mismatch"); }
        ulong lba = BitConverter.ToUInt64(header, 72); uint count = BitConverter.ToUInt32(header, 80), stride = BitConverter.ToUInt32(header, 84);
        if (count is < 1 or > 1024 || stride is < 128 or > 1024 || stride % 8 != 0) { throw new IOException("unsupported GPT identity entries"); }
        int bytes = checked((int)(count * stride)); ulong sectors = ((ulong)bytes + 511) / 512;
        if (lba < 2 || lba >= disk.BlockCount || sectors > disk.BlockCount - lba) { throw new IOException("GPT entries outside disk"); }
        byte[] entries = new byte[checked((int)sectors * 512)]; disk.ReadBlock(lba, sectors, entries);
        if (GptChecksums.Crc32(entries, bytes) != BitConverter.ToUInt32(header, 88)) { throw new IOException("GPT entries checksum mismatch"); }
        var result = new List<GptIdentity>(); var ids = new HashSet<Guid>();
        for (int i = 0; i < count; i++)
        {
            int offset = checked(i * (int)stride); Guid type = new(entries.AsSpan(offset, 16));
            if (type == Guid.Empty) { continue; }
            Guid id = new(entries.AsSpan(offset + 16, 16)); ulong start = BitConverter.ToUInt64(entries, offset + 32), end = BitConverter.ToUInt64(entries, offset + 40);
            if (id == Guid.Empty || !ids.Add(id) || start < 34 || end < start || end >= disk.BlockCount - 33)
            { throw new IOException("invalid/duplicate GPT partition identity"); }
            foreach (var other in result)
            { if (start < other.Start + other.Count && other.Start <= end) { throw new IOException("overlapping GPT partitions"); } }
            result.Add(new(type, id, start, end - start + 1));
        }
        return result.AsReadOnly();
    }
}
