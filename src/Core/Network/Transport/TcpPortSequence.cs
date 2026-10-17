using System;
using System.IO;
using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>A seeded permutation of the 16K dynamic ports; active-port exclusion stays in Tcp.</summary>
public sealed class TcpPortSequence
{
    private const int Mask = 16383;
    private readonly int _start, _stride;
    private int _position = -1;

    public TcpPortSequence(uint seed)
    {
        _start = (int)(seed & Mask);
        // Odd steps visit every position modulo 2^14 before repeating.
        _stride = (int)((seed >> 14) & Mask) | 1;
    }

    public ushort Next()
    {
        int position = Interlocked.Increment(ref _position) & Mask;
        return (ushort)(49152 + ((_start + position * _stride) & Mask));
    }
}

/// <summary>Initialized from hardware entropy before the network service opens a TCP socket.</summary>
public static class TcpPortAllocation
{
    private static TcpPortSequence? s_sequence;
    public static bool Initialized => s_sequence is not null;

    public static void Initialize(uint seed)
    {
        if (s_sequence is null)
        { Interlocked.CompareExchange(ref s_sequence, new TcpPortSequence(seed), null); }
    }

    public static ushort Next()
        => (s_sequence ?? throw new IOException("TCP port allocator requires hardware entropy")).Next();
}
