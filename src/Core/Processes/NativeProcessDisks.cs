using System;
using System.Buffers.Binary;
using System.Text;
using Zenith.Core.Storage;

namespace Zenith.Core.Processes;

/// <summary>zenith-native-disk-abi 1.0 (userland/include/zenith_disks.h): the 16384-byte snapshot
/// (647) and the 72-byte edit request (648) over <see cref="DiskService"/>. Since kernel 0.0.54 the
/// snapshot's reserved tail carries extras version 1 (used bytes, filesystem state, GPT copies);
/// older kernels leave it zero, and clients check extra.version.</summary>
internal static class ProcessDisks
{
    public const int SnapshotBytes = 16384, RequestBytes = 72;
    private const int Header = 200, DiskBytes = 1832, PartBytes = 104, MaxDisks = 8, MaxParts = 16, MaxFree = 8;
    /// <summary>struct zenith_disks_extra: version, reserved, used[8][16], state[8][16], gpt[8].</summary>
    private const int Extra = Header + MaxDisks * DiskBytes, ExtraUsed = Extra + 8, ExtraState = ExtraUsed + MaxDisks * MaxParts * 8,
        ExtraGpt = ExtraState + MaxDisks * MaxParts;

    public static byte[] Snapshot(uint uid)
    {
        byte[] data = new byte[SnapshotBytes];
        Span<byte> b = data;
        var job = DiskService.Job;
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], uid == 0 ? 1u : 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], DiskService.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)job.State);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], (uint)job.Action);
        BinaryPrimitives.WriteUInt64LittleEndian(b[24..], job.Token);
        Text(b.Slice(32, 160), job.Message);
        var disks = DiskService.Describe();
        int count = Math.Min(disks.Count, MaxDisks);
        BinaryPrimitives.WriteUInt32LittleEndian(b[192..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[Extra..], 1);
        for (int d = 0; d < count; d++)
        {
            var disk = disks[d];
            Span<byte> o = b.Slice(Header + d * DiskBytes, DiskBytes);
            Text(o[..16], disk.Name);
            BinaryPrimitives.WriteUInt64LittleEndian(o[16..], disk.Sectors);
            uint flags = (disk.Gpt ? 1u : 0) | (disk.System ? 2u : 0) | (disk.Mounted ? 4u : 0) | (disk.Editable ? 8u : 0);
            b[ExtraGpt + d] = (byte)(!disk.Gpt ? 0 : disk.GptCopiesMatch ? 1 : 2);
            BinaryPrimitives.WriteUInt32LittleEndian(o[24..], flags);
            int parts = Math.Min(disk.Parts.Count, MaxParts), free = Math.Min(disk.Free.Count, MaxFree);
            BinaryPrimitives.WriteUInt32LittleEndian(o[28..], (uint)parts);
            BinaryPrimitives.WriteUInt32LittleEndian(o[32..], (uint)free);
            for (int i = 0; i < parts; i++)
            {
                var p = disk.Parts[i];
                Span<byte> q = o.Slice(40 + i * PartBytes, PartBytes);
                Text(q[..16], p.Name);
                BinaryPrimitives.WriteUInt64LittleEndian(q[16..], p.Start);
                BinaryPrimitives.WriteUInt64LittleEndian(q[24..], p.Sectors);
                BinaryPrimitives.WriteUInt64LittleEndian(q[32..], p.GrowLimit);
                BinaryPrimitives.WriteUInt32LittleEndian(q[40..], (uint)p.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(q[44..], (uint)p.Filesystem);
                BinaryPrimitives.WriteUInt32LittleEndian(q[48..], p.BlockSize);
                BinaryPrimitives.WriteUInt32LittleEndian(q[52..], (uint)p.Role);
                Text(q.Slice(56, 16), p.Label);
                Text(q.Slice(72, 32), p.Mount);
                BinaryPrimitives.WriteUInt64LittleEndian(b[(ExtraUsed + (d * MaxParts + i) * 8)..], p.UsedBytes);
                b[ExtraState + d * MaxParts + i] = (byte)p.State;
            }
            for (int i = 0; i < free; i++)
            {
                Span<byte> r = o.Slice(40 + MaxParts * PartBytes + i * 16, 16);
                BinaryPrimitives.WriteUInt64LittleEndian(r, disk.Free[i].Start);
                BinaryPrimitives.WriteUInt64LittleEndian(r[8..], disk.Free[i].Sectors);
            }
        }
        return data;
    }

    /// <summary>Bounded, NUL-terminated ASCII; anything else becomes '?'.</summary>
    private static void Text(Span<byte> field, string text)
    {
        int n = Math.Min(text.Length, field.Length - 1);
        for (int i = 0; i < n; i++) { char c = text[i]; field[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?'; }
    }

    private static string? Field(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0) { return null; }
        foreach (byte c in field[..end]) { if (c is < 0x20 or > 0x7e) { return null; } }
        foreach (byte c in field[end..]) { if (c != 0) { return null; } }
        return Encoding.ASCII.GetString(field[..end]);
    }

    // Request: version 0, action 4, revision 8, disk[16] 16, start 32, sectors 40, filesystem 48,
    // reserved 52, label[16] 56.
    public static long Request(ReadOnlySpan<byte> b, uint uid)
    {
        if (b.Length != RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(b[52..]) != 0) { return -22; }
        uint action = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), filesystem = BinaryPrimitives.ReadUInt32LittleEndian(b[48..]);
        string? disk = Field(b.Slice(16, 16)), label = Field(b.Slice(56, 16));
        if (action is < 1 or > 4 || disk is null || disk.Length == 0 || label is null || filesystem > 1) { return -22; }
        ulong start = BinaryPrimitives.ReadUInt64LittleEndian(b[32..]), sectors = BinaryPrimitives.ReadUInt64LittleEndian(b[40..]);
        if (sectors != 0 && (sectors % DiskLayout.Alignment != 0 || sectors < DiskLayout.MinimumSectors)) { return -22; }
        return DiskService.Submit(uid, (DiskService.Action)action, BinaryPrimitives.ReadUInt64LittleEndian(b[8..]),
            disk, start, sectors, filesystem == 1, label);
    }
}
