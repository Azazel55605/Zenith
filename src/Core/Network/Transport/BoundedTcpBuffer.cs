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
        public void CopyPrefix(Span<byte> destination)
        {
            if (destination.Length > Count) { throw new ArgumentOutOfRangeException(nameof(destination)); }
            for (int i = 0; !destination.IsEmpty; i++)
            {
                var chunk = Chunks[i].AsSpan(i == 0 ? Offset : 0);
                int length = Math.Min(chunk.Length, destination.Length);
                chunk[..length].CopyTo(destination);
                destination = destination[length..];
            }
        }
        public ReadOnlySpan<byte> Data
        {
            get
            {
                if (Count == 0) { return ReadOnlySpan<byte>.Empty; }
                if (Chunks.Length == 1) { return Chunks[0].AsSpan(Offset, Count); }
                byte[]? cached = Volatile.Read(ref _snapshot);
                if (cached is not null) { return cached; }
                var bytes = new byte[Count];
                CopyPrefix(bytes);
                return Interlocked.CompareExchange(ref _snapshot, bytes, null) ?? bytes;
            }
        }
    }
    private State _state = new(Array.Empty<byte[]>(), 0, 0);
    // Like the connection's RCV.NXT, this receive-side state is owned by the NIC receive path.
    // Socket readers only access the immutable, already contiguous State above.
    private TcpReorderWindow? _pending;

    public int Receive(uint expected, uint sequence, byte[] bytes, bool fin)
    {
        int delta = unchecked((int)(sequence - expected));
        long end = (long)delta + bytes.Length;
        if (delta == int.MinValue || end <= 0 || end > Window || (fin && delta != 0)) { return 0; }
        var fresh = bytes.AsSpan(delta < 0 ? -delta : 0);
        if (delta <= 0 && (_pending is null || _pending.Empty))
        {
            return Append(fresh) ? fresh.Length : -1;
        }
        var pending = _pending ??= new TcpReorderWindow();
        int inserted = pending.Store(delta < 0 ? expected : sequence, fresh);
        if (delta > 0)
        {
            if (inserted > 0) { TcpSegmentPolicy.Buffered(); }
            return 0;
        }
        // A FIN consumes the sequence number directly following its payload.
        // Never expose buffered bytes beyond that point before the original FSM observes FIN.
        byte[] ready = pending.Take(expected, fin ? fresh.Length : Window);
        if (fin) { _pending = null; }
        if (!Append(ready)) { return -1; }
        if (ready.Length > inserted) { TcpSegmentPolicy.Recovered(ready.Length - inserted); }
        return ready.Length;
    }

    public ushort Window
    {
        get { var state = Volatile.Read(ref _state); return state.Overflow ? (ushort)0 : (ushort)Math.Max(0, ReceiveWindow - state.Count); }
    }
    public ReadOnlySpan<byte> Data => Volatile.Read(ref _state).Data;
    public int Count => Volatile.Read(ref _state).Count;
    public void CopyPrefix(Span<byte> destination) => Volatile.Read(ref _state).CopyPrefix(destination);
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
                // Keep full-size TCP segments intact. Pack small writes to bound the
                // number of chunk references even when the peer sends one byte at a time.
                int merge = 0;
                if (before.Chunks.Length > 0)
                {
                    int tail = before.Chunks[^1].Length;
                    if (tail < 1024 || bytes.Length < 1024)
                    {
                        merge = Math.Min(bytes.Length, ChunkSize - tail);
                    }
                }
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
            var chunks = before.Chunks;
            if (first == chunks.Length) { chunks = Array.Empty<byte[]>(); }
            else if (first != 0)
            {
                chunks = new byte[before.Chunks.Length - first][];
                Array.Copy(before.Chunks, first, chunks, 0, chunks.Length);
            }
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
    public static int Count(ref BoundedTcpBuffer? buffer) => Get(ref buffer).Count;
    public static void CopyPrefix(ref BoundedTcpBuffer? buffer, Span<byte> destination) => Get(ref buffer).CopyPrefix(destination);
    public static bool Append(ref BoundedTcpBuffer? buffer, ReadOnlySpan<byte> bytes) => Get(ref buffer).Append(bytes);
    public static int Receive(ref BoundedTcpBuffer? buffer, uint expected, uint sequence, byte[] bytes, bool fin) =>
        Get(ref buffer).Receive(expected, sequence, bytes, fin);
    public static ushort Window(ref BoundedTcpBuffer? buffer) => Get(ref buffer).Window;
    public static void Advance(ref BoundedTcpBuffer? buffer, int count) => Get(ref buffer).Advance(count);
    public static void Release(ref BoundedTcpBuffer? buffer) => Interlocked.Exchange(ref buffer, null);
}
