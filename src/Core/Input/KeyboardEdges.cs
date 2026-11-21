using System;
using System.Threading;

namespace Zenith.Core.Input;

/// <summary>Desktop-only physical edges from Cosmos before it discards releases.
/// Producers can run in interrupt context and must neither allocate nor wait.</summary>
public static class KeyboardEdges
{
    public const int Capacity = 128;
    private static ushort[]? s_events;
    private static bool[]? s_held;
    private static int s_enabled, s_gate, s_lost, s_count;

    public static void SetEnabled(bool enabled)
    {
        Volatile.Write(ref s_enabled, 0);
        // The desktop calls this with local interrupts masked. Other producers
        // never wait for the gate and have no blocking work inside it.
        while (Interlocked.CompareExchange(ref s_gate, 1, 0) != 0) { }
        if (enabled)
        {
            s_events ??= new ushort[Capacity];
            s_held ??= new bool[256];
        }
        s_count = 0;
        if (s_held is not null) { Array.Clear(s_held); }
        Volatile.Write(ref s_lost, 0);
        Volatile.Write(ref s_enabled, enabled ? 1 : 0);
        Volatile.Write(ref s_gate, 0);
    }

    public static void Capture(byte scan, bool released)
    {
        if (scan == 0 || Volatile.Read(ref s_enabled) == 0) { return; }
        if (Interlocked.CompareExchange(ref s_gate, 1, 0) != 0)
        {
            Volatile.Write(ref s_lost, 2);
            return;
        }

        if (s_enabled != 0 && s_held![scan] == released)
        {
            s_held[scan] = !released;
            if (s_count == Capacity) { Interlocked.CompareExchange(ref s_lost, 1, 0); }
            else { s_events![s_count++] = (ushort)(scan | (released ? 256 : 0)); }
        }
        Volatile.Write(ref s_gate, 0);
    }

    /// <summary>-1 means the batch was lost. The consumer must clear delivered holds.</summary>
    public static int Drain(Span<ushort> destination)
    {
        if (Interlocked.CompareExchange(ref s_gate, 1, 0) != 0)
        {
            Interlocked.CompareExchange(ref s_lost, 1, 0);
            return -1;
        }
        int lost = Interlocked.Exchange(ref s_lost, 0);
        if (lost == 2 && s_held is not null) { Array.Clear(s_held); }
        int count = s_count;
        s_count = 0;
        if (lost != 0 || count > destination.Length) { count = -1; }
        else if (count != 0) { s_events.AsSpan(0, count).CopyTo(destination); }
        Volatile.Write(ref s_gate, 0);
        return count;
    }
}
