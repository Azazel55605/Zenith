using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Zenith.Core.Processes;

namespace Zenith.Core.Monitoring;

/// <summary>
/// Observatory's process list (call 643): the caller's own programs, or all of them for root, as
/// fixed records. Exited programs stay listed briefly with their status, flagged, so a client can
/// show what just ended. Names and accounts are clipped, never truncated mid-byte.
/// </summary>
internal static class ProcessMonitor
{
    public const int Bytes = 4096, HeaderBytes = 16, EntryBytes = 64, MaximumEntries = (Bytes - HeaderBytes) / EntryBytes;
    public const byte StopRequested = 1, Exited = 2;

    public static byte[] Snapshot(IReadOnlyList<ProcessInfo> list, Func<uint, string> account)
    {
        var data = new byte[Bytes]; Span<byte> b = data;
        // Live programs first, newest exits after them, so a full list drops the oldest exits.
        var ordered = new List<ProcessInfo>(list);
        ordered.Sort((x, y) => (x.State == ProcessState.Exited) != (y.State == ProcessState.Exited)
            ? (x.State == ProcessState.Exited ? 1 : -1)
            : x.State == ProcessState.Exited ? y.Pid.CompareTo(x.Pid) : x.Pid.CompareTo(y.Pid));
        int count = Math.Min(MaximumEntries, ordered.Count);
        var names = new Dictionary<uint, string>();
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], (uint)ordered.Count);
        for (int i = 0; i < count; i++)
        {
            var p = ordered[i]; var e = b.Slice(HeaderBytes + i * EntryBytes, EntryBytes);
            BinaryPrimitives.WriteInt32LittleEndian(e, p.Pid);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], p.Uid);
            e[8] = (byte)p.State;
            e[9] = (byte)((p.StopRequested ? StopRequested : 0) | (p.State == ProcessState.Exited ? Exited : 0));
            e[10] = (byte)(sbyte)Math.Clamp(p.Cpu, -1, 127);
            e[11] = (byte)p.Affinity;
            BinaryPrimitives.WriteInt32LittleEndian(e[12..], p.Status ?? -1);
            Ascii(p.Name, e.Slice(16, 32));
            if (!names.TryGetValue(p.Uid, out string? user)) { user = account(p.Uid); names[p.Uid] = user; }
            Ascii(user, e.Slice(48, 16));
        }
        return data;
    }

    private static void Ascii(string text, Span<byte> field)
    {
        int n = 0;
        foreach (char c in text) { if (n == field.Length - 1) { break; } field[n++] = c is >= ' ' and < (char)127 ? (byte)c : (byte)'?'; }
    }
}
