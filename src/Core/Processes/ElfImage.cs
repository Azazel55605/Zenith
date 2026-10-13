using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;

namespace Zenith.Core.Processes;

internal sealed record ElfSegment(ulong Address, int Offset, int FileBytes, int MemoryBytes, uint Flags)
{
    public int Pages => (MemoryBytes + 4095) / 4096;
}
internal sealed record ElfImage(ulong Entry, IReadOnlyList<ElfSegment> Segments)
{
    public const int MaximumBytes = 262144;
    public const ulong MinimumAddress = 0x400000, MaximumAddress = 0x800000;
    public static ElfImage Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 64 or > MaximumBytes || bytes[0] != 127 || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F'
            || bytes[4] != 2 || bytes[5] != 1 || bytes[6] != 1 || U16(bytes, 16) != 2 || U16(bytes, 18) != 62
            || U32(bytes, 20) != 1 || U16(bytes, 52) != 64 || U16(bytes, 54) != 56)
        { throw new IOException("expected bounded static x86_64 ELF64 executable"); }
        ulong entry = U64(bytes, 24), table = U64(bytes, 32); int count = U16(bytes, 56);
        if (count is < 1 or > 16 || table < 64 || table > (ulong)bytes.Length || (ulong)(count * 56) > (ulong)bytes.Length - table)
        { throw new IOException("invalid ELF program table"); }
        var segments = new List<ElfSegment>(); bool abi = false;
        for (int i = 0; i < count; i++)
        {
            int header = (int)table + i * 56; uint type = U32(bytes, header), flags = U32(bytes, header + 4);
            ulong offset = U64(bytes, header + 8), address = U64(bytes, header + 16), length = U64(bytes, header + 32), memory = U64(bytes, header + 40);
            if (offset > (ulong)bytes.Length || length > (ulong)bytes.Length - offset) { throw new IOException("ELF segment file bounds"); }
            if (type == 4)
            {
                var notes = bytes.Slice((int)offset, (int)length); int position = 0;
                while (position < notes.Length)
                {
                    if (notes.Length - position < 12) { throw new IOException("truncated ELF note"); }
                    uint name = U32(notes, position), desc = U32(notes, position + 4), kind = U32(notes, position + 8);
                    if (name > 32 || desc > 64) { throw new IOException("oversized ELF note"); }
                    int paddedName = ((int)name + 3) & ~3, paddedDesc = ((int)desc + 3) & ~3;
                    if (12 + paddedName + paddedDesc > notes.Length - position) { throw new IOException("ELF note bounds"); }
                    if (name == 7 && notes.Slice(position + 12, 7).SequenceEqual("ZENITH\0"u8))
                    {
                        if (abi || kind != 1 || desc != 4 || U32(notes, position + 12 + paddedName) != 1)
                        { throw new IOException("unsupported or duplicate Zenith native ABI"); }
                        abi = true;
                    }
                    position += 12 + paddedName + paddedDesc;
                }
            }
            else if (type == 1)
            {
                if (flags is not (4 or 5 or 6) || length > memory || memory == 0 || memory > MaximumAddress - MinimumAddress
                    || address < MinimumAddress || address >= MaximumAddress || memory > MaximumAddress - address
                    || address % 4096 != 0 || offset % 4096 != 0 || U64(bytes, header + 48) != 4096)
                { throw new IOException("ELF load bounds, page alignment or W^X violation"); }
                var segment = new ElfSegment(address, (int)offset, (int)length, (int)memory, flags);
                foreach (var previous in segments)
                { if (address < previous.Address + (ulong)(previous.Pages * 4096) && previous.Address < address + (ulong)(segment.Pages * 4096)) { throw new IOException("overlapping ELF pages"); } }
                segments.Add(segment);
            }
            else if (type != 0 && type != 0x6474e551) { throw new IOException("dynamic, TLS or unsupported ELF program type"); }
            else if (type == 0x6474e551 && (flags & 1) != 0) { throw new IOException("executable user stack refused"); }
        }
        if (!abi || segments.Count == 0 || !segments.Exists(segment => segment.Flags == 5 && entry >= segment.Address && entry - segment.Address < (ulong)segment.FileBytes))
        { throw new IOException("missing native ABI or file-backed executable entry"); }
        return new(entry, segments.AsReadOnly());
    }
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
}
