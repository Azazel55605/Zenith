using System;
using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Immutable snapshots survive concurrent append/read; CAS avoids taking a lock in NIC callbacks.</summary>
public sealed class BoundedTcpBuffer
{
    public const int Maximum = 128 * 1024;
    private sealed class State
    {
        public State(byte[] bytes, bool overflow = false) { Bytes = bytes; Overflow = overflow; }
        public readonly byte[] Bytes;
        public readonly bool Overflow;
    }
    private State _state = new(Array.Empty<byte>());
    public ushort Window
    {
        get { var state = Volatile.Read(ref _state); return state.Overflow ? (ushort)0 : (ushort)Math.Max(0, 8192 - state.Bytes.Length); }
    }
    public ReadOnlySpan<byte> Data => Volatile.Read(ref _state).Bytes;
    public bool Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) { return !Volatile.Read(ref _state).Overflow; }
        while (true)
        {
            State before = Volatile.Read(ref _state);
            if (before.Overflow) { return false; }
            bool overflow = bytes.Length > Maximum - before.Bytes.Length;
            byte[] next = overflow ? Array.Empty<byte>() : new byte[before.Bytes.Length + bytes.Length];
            if (!overflow) { before.Bytes.CopyTo(next, 0); bytes.CopyTo(next.AsSpan(before.Bytes.Length)); }
            var after = new State(next, overflow);
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
            if (count > before.Bytes.Length) { throw new ArgumentOutOfRangeException(nameof(count)); }
            var after = new State(before.Bytes.AsSpan(count).ToArray());
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
