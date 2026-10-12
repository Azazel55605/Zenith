using System;
using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Immutable snapshots survive concurrent append/read; CAS avoids taking a lock in NIC callbacks.</summary>
public sealed class BoundedTcpBuffer
{
    public const int Maximum = 128 * 1024;
    // Bulk window with headroom for the current small NIC receive ring.
    public const int ReceiveWindow = 32768;
    private const int ChunkSize = 4096;
    private sealed class State
    {
        public State(byte[][] chunks, int offset, int count, bool overflow = false)
        { Chunks = chunks; Offset = offset; Count = count; Overflow = overflow; }
        public readonly byte[][] Chunks;
        public readonly int Offset, Count;
        public readonly bool Overflow;
        private byte[]? _snapshot;
        public ReadOnlySpan<byte> Data
        {
            get
            {
                if (Count == 0) { return ReadOnlySpan<byte>.Empty; }
                if (Chunks.Length == 1) { return Chunks[0].AsSpan(Offset, Count); }
                byte[]? cached = Volatile.Read(ref _snapshot);
                if (cached is not null) { return cached; }
                var bytes = new byte[Count]; int written = 0;
                for (int i = 0; i < Chunks.Length; i++)
                {
                    var chunk = Chunks[i].AsSpan(i == 0 ? Offset : 0);
                    chunk.CopyTo(bytes.AsSpan(written)); written += chunk.Length;
                }
                return Interlocked.CompareExchange(ref _snapshot, bytes, null) ?? bytes;
            }
        }
    }
    private State _state = new(Array.Empty<byte[]>(), 0, 0);
    public ushort Window
    {
        get { var state = Volatile.Read(ref _state); return state.Overflow ? (ushort)0 : (ushort)Math.Max(0, ReceiveWindow - state.Count); }
    }
    public ReadOnlySpan<byte> Data => Volatile.Read(ref _state).Data;
    public bool Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) { return !Volatile.Read(ref _state).Overflow; }
        while (true)
        {
            State before = Volatile.Read(ref _state);
            if (before.Overflow) { return false; }
            bool overflow = bytes.Length > Maximum - before.Count;
            State after;
            if (overflow) { after = new State(Array.Empty<byte[]>(), 0, 0, true); }
            else
            {
                // Copy at most the last 4 KiB chunk, rather than the entire
                // unread window on every packet. Earlier chunks stay immutable.
                int merge = before.Chunks.Length == 0 ? 0 : Math.Min(bytes.Length, ChunkSize - before.Chunks[^1].Length);
                int added = (bytes.Length - merge + ChunkSize - 1) / ChunkSize;
                var chunks = new byte[before.Chunks.Length + added][];
                before.Chunks.CopyTo(chunks, 0);
                if (merge != 0)
                {
                    var tail = before.Chunks[^1]; var replacement = new byte[tail.Length + merge];
                    tail.CopyTo(replacement, 0); bytes.Slice(0, merge).CopyTo(replacement.AsSpan(tail.Length));
                    chunks[before.Chunks.Length - 1] = replacement;
                }
                int copied = merge;
                for (int i = before.Chunks.Length; i < chunks.Length; i++)
                {
                    int count = Math.Min(ChunkSize, bytes.Length - copied);
                    chunks[i] = bytes.Slice(copied, count).ToArray(); copied += count;
                }
                after = new State(chunks, before.Offset, before.Count + bytes.Length);
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, after, before), before)) { return !overflow; }
        }
    }
    public void Advance(int count)
    {
        if (count < 0) { throw new ArgumentOutOfRangeException(nameof(count)); }
        if (count == 0) { return; }
        while (true)
        {
            State before = Volatile.Read(ref _state);
            // Overflow retires all data. The reader may still hold a valid older snapshot.
            if (before.Overflow) { return; }
            if (count > before.Count) { throw new ArgumentOutOfRangeException(nameof(count)); }
            int offset = before.Offset + count, first = 0;
            while (first < before.Chunks.Length && offset >= before.Chunks[first].Length)
            { offset -= before.Chunks[first++].Length; }
            var chunks = new byte[before.Chunks.Length - first][];
            Array.Copy(before.Chunks, first, chunks, 0, chunks.Length);
            var after = new State(chunks, offset, before.Count - count);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, after, before), before)) { return; }
        }
    }
}

/// <summary>Public ABI used only by the signature-checked Cosmos transport build adaptation.</summary>
public static class TcpReceiveBridge
{
    private static BoundedTcpBuffer Get(ref BoundedTcpBuffer? buffer)
    {
        BoundedTcpBuffer? existing = Volatile.Read(ref buffer);
        if (existing is not null) { return existing; }
        var created = new BoundedTcpBuffer();
        return Interlocked.CompareExchange(ref buffer, created, null) ?? created;
    }
    public static ReadOnlySpan<byte> Data(ref BoundedTcpBuffer? buffer) => Get(ref buffer).Data;
    public static bool Append(ref BoundedTcpBuffer? buffer, ReadOnlySpan<byte> bytes) => Get(ref buffer).Append(bytes);
    public static ushort Window(ref BoundedTcpBuffer? buffer) => Get(ref buffer).Window;
    public static void Advance(ref BoundedTcpBuffer? buffer, int count) => Get(ref buffer).Advance(count);
    public static void Release(ref BoundedTcpBuffer? buffer) => Interlocked.Exchange(ref buffer, null);
}
