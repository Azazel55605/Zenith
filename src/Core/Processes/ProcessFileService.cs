using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Zenith.Core.Files;

namespace Zenith.Core.Processes;

/// <summary>One directory entry as call 654 reports it.</summary>
internal readonly record struct FileListEntry(string Name, uint Mode, uint Uid, uint Gid, ulong Size, long Mtime, uint Links, int Items, uint TargetMode);

/// <summary>zenith-native-files-abi 1.0 (userland/include/zenith_files.h): 654 list (1040-byte request, 32768-byte
/// page), 655 filesystem (128 bytes), 656 start a transfer job, 657 its status (512 bytes), 658 control.
/// Encoding only; the kernel's VFS side is <c>NativeFileService</c>.</summary>
internal static class ProcessFileService
{
    public const int ListRequestBytes = 1040, PageBytes = 32768, PageHeader = 48, EntryHeader = 48, FilesystemBytes = 128,
        JobHeader = 24, MaximumJobRequest = 8192, StatusBytes = 512, MaximumEntries = 4096, CountedFolders = 128;
    public const uint CountItems = 1;

    /// <summary>Request: version 0, flags 4, offset 8, path_bytes 12, path[1024] 16.</summary>
    public static bool ParseList(ReadOnlySpan<byte> b, out string path, out uint flags, out int offset)
    {
        path = ""; flags = 0; offset = 0;
        if (b.Length != ListRequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1) { return false; }
        flags = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]), length = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
        if (flags > CountItems || start > MaximumEntries || length is < 1 or > 1024) { return false; }
        foreach (byte value in b.Slice(16 + (int)length, 1024 - (int)length)) { if (value != 0) { return false; } }
        if (Path(b.Slice(16, (int)length)) is not string text) { return false; }
        path = text; offset = (int)start; return true;
    }

    /// <summary>Header: version 0, count 4, total 8, next 12, folder mtime 16, folder mode 24, flags 28 (bit 0:
    /// the caller may add and remove entries), now 32 (UTC seconds), the local zone's offset now 40 (seconds),
    /// reserved 44. Entries from offset, each 8-byte aligned: record 0 (u16), name bytes 2 (u16), mode 4, uid 8,
    /// gid 12, size 16, mtime 24, links 32, items 36, target mode 40, the zone's offset at mtime 44, name 48
    /// (NUL-terminated).</summary>
    public static byte[] Page(IReadOnlyList<FileListEntry> entries, int offset, long folderMtime, uint folderMode, bool writable, out int count)
        => Page(entries, offset, folderMtime, folderMode, writable, 0, _ => 0, out count);
    public static byte[] Page(IReadOnlyList<FileListEntry> entries, int offset, long folderMtime, uint folderMode, bool writable,
        long now, Func<long, int> zoneOffset, out int count)
    {
        byte[] data = new byte[PageBytes]; Span<byte> b = data;
        int at = PageHeader; count = 0; int index = offset;
        for (; index < entries.Count; index++)
        {
            var e = entries[index];
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            if (name.Length > 255) { name = name.AsSpan(0, 255).ToArray(); }
            int record = (EntryHeader + name.Length + 1 + 7) & ~7;
            if (at + record > PageBytes) { break; }
            Span<byte> r = b.Slice(at, record);
            BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)record);
            BinaryPrimitives.WriteUInt16LittleEndian(r[2..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], e.Mode);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], e.Uid);
            BinaryPrimitives.WriteUInt32LittleEndian(r[12..], e.Gid);
            BinaryPrimitives.WriteUInt64LittleEndian(r[16..], e.Size);
            BinaryPrimitives.WriteInt64LittleEndian(r[24..], e.Mtime);
            BinaryPrimitives.WriteUInt32LittleEndian(r[32..], e.Links);
            BinaryPrimitives.WriteInt32LittleEndian(r[36..], e.Items);
            BinaryPrimitives.WriteUInt32LittleEndian(r[40..], e.TargetMode);
            BinaryPrimitives.WriteInt32LittleEndian(r[44..], zoneOffset(e.Mtime));
            name.CopyTo(r[EntryHeader..]);
            at += record; count++;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], (uint)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], index < entries.Count ? (uint)index : 0);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], folderMtime);
        BinaryPrimitives.WriteUInt32LittleEndian(b[24..], folderMode);
        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], writable ? 1u : 0);
        BinaryPrimitives.WriteInt64LittleEndian(b[32..], now);
        BinaryPrimitives.WriteInt32LittleEndian(b[40..], zoneOffset(now));
        return data;
    }

    /// <summary>version 0, flags 4 (1 read-only, 2 Unix ownership and permissions), total 8, available 16,
    /// block size 24, mount point[64] 32, type[16] 96.</summary>
    public static byte[] Filesystem(ulong total, ulong available, ulong block, bool readOnly, bool unix, string mount, string type)
    {
        byte[] data = new byte[FilesystemBytes]; Span<byte> b = data;
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (readOnly ? 1u : 0) | (unix ? 2u : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(b[8..], total);
        BinaryPrimitives.WriteUInt64LittleEndian(b[16..], available);
        BinaryPrimitives.WriteUInt64LittleEndian(b[24..], block);
        Text(b.Slice(32, 64), mount); Text(b.Slice(96, 16), type);
        return data;
    }

    /// <summary>Request: version 0, operation 4, flags 8, count 12, text_bytes 16, reserved 20, then the destination
    /// and each source, NUL-terminated. Delete has an empty destination.</summary>
    public static bool ParseJob(ReadOnlySpan<byte> b, out FileJobOperation operation, out uint flags, out string destination, out string[] sources)
    {
        operation = 0; flags = 0; destination = ""; sources = Array.Empty<string>();
        if (b.Length is < JobHeader or > MaximumJobRequest || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(b[20..]) != 0) { return false; }
        uint op = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), count = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
        flags = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]);
        if (op is < 1 or > 3 || count is < 1 or > FileJob.MaximumSources || BinaryPrimitives.ReadUInt32LittleEndian(b[16..]) != b.Length - JobHeader) { return false; }
        var parts = new List<string>();
        var text = b[JobHeader..]; int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != 0) { continue; }
            if (i - start > 1024) { return false; }
            string? part = i == start ? "" : Path(text[start..i]);
            if (part is null) { return false; }
            parts.Add(part); start = i + 1;
        }
        if (start != text.Length || parts.Count != count + 1) { return false; }
        operation = (FileJobOperation)op; destination = parts[0];
        sources = parts.GetRange(1, (int)count).ToArray();
        foreach (string source in sources) { if (source.Length == 0) { return false; } }
        return true;
    }

    /// <summary>version 0, state 4, operation 8, errors 12, items done 16, items total 20, bytes done 24, bytes total
    /// 32, question kind 40, reserved 44, current[160] 48, message[160] 208, question[144] 368.</summary>
    public static byte[] Status(FileJobStatus s)
    {
        byte[] data = new byte[StatusBytes]; Span<byte> b = data;
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)s.State);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], (uint)s.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)s.Errors);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)s.ItemsDone);
        BinaryPrimitives.WriteUInt32LittleEndian(b[20..], (uint)s.ItemsTotal);
        BinaryPrimitives.WriteUInt64LittleEndian(b[24..], s.BytesDone);
        BinaryPrimitives.WriteUInt64LittleEndian(b[32..], s.BytesTotal);
        BinaryPrimitives.WriteUInt32LittleEndian(b[40..], (uint)s.QuestionKind);
        Text(b.Slice(48, 160), s.Current); Text(b.Slice(208, 160), s.Message); Text(b.Slice(368, 144), s.Question);
        return data;
    }

    /// <summary>658: 1 cancel, 2 answer (value 1..4, plus 256 for every later question), 3 forget.</summary>
    public static long Control(FileJob? job, int pid, long id, ulong command, ulong value)
    {
        if (job is null) { return -9; }
        if (command == 1) { if (value != 0) { return -22; } job.Cancel(); return 0; }
        if (command == 2)
        {
            if ((value & ~0x1ffUL) != 0) { return -22; }
            return job.Answer((FileJobAnswer)(value & 0xff), (value & 256) != 0) ? 0 : -22;
        }
        if (command == 3) { return value != 0 ? -22 : FileJobs.Forget(id, pid); }
        return -22;
    }

    /// <summary>An absolute or relative UTF-8 path without NUL or control characters.</summary>
    private static string? Path(ReadOnlySpan<byte> bytes)
    {
        // Validated by hand: no exceptions on the service path.
        for (int i = 0; i < bytes.Length;)
        {
            byte c = bytes[i];
            if (c < 0x20 || c == 0x7f) { return null; }
            int extra = c < 0x80 ? 0 : (c & 0xE0) == 0xC0 && c >= 0xC2 ? 1 : (c & 0xF0) == 0xE0 ? 2 : (c & 0xF8) == 0xF0 && c <= 0xF4 ? 3 : -1;
            if (extra < 0) { return null; }
            for (int k = 1; k <= extra; k++) { if (i + k >= bytes.Length || (bytes[i + k] & 0xC0) != 0x80) { return null; } }
            if (extra == 2 && ((c == 0xE0 && bytes[i + 1] < 0xA0) || (c == 0xED && bytes[i + 1] >= 0xA0))) { return null; }
            if (extra == 3 && ((c == 0xF0 && bytes[i + 1] < 0x90) || (c == 0xF4 && bytes[i + 1] >= 0x90))) { return null; }
            i += extra + 1;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>UTF-8 cut at a character boundary, always NUL-terminated.</summary>
    public static void Text(Span<byte> field, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int n = Math.Min(bytes.Length, field.Length - 1);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) { n--; }
        bytes.AsSpan(0, n).CopyTo(field);
    }
}
