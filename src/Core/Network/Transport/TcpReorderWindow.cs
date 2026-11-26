using System;

namespace Zenith.Core.Network.Transport;

/// <summary>First-arrival-wins byte storage for one advertised window, allocated only when a gap occurs.</summary>
internal sealed class TcpReorderWindow
{
    // Every admissible sequence maps to a different slot, including across the 32-bit TCP wrap.
    private const int Capacity = BoundedTcpBuffer.ReceiveWindow;
    private readonly byte[] _bytes = new byte[Capacity];
    private readonly uint[] _present = new uint[Capacity / 32];
    private int _count;
    internal bool Empty => _count == 0;

    internal int Store(uint sequence, ReadOnlySpan<byte> data)
    {
        int added = 0;
        for (int i = 0; i < data.Length; i++)
        {
            int slot = (int)(unchecked(sequence + (uint)i) % Capacity);
            uint bit = 1u << (slot & 31);
            ref uint word = ref _present[slot >> 5];
            if ((word & bit) == 0)
            {
                word |= bit;
                _bytes[slot] = data[i];
                added++;
            }
        }
        _count += added;
        return added;
    }

    internal byte[] Take(uint expected, int limit)
    {
        int start = (int)(expected % Capacity), length = 0;
        while (length < limit && length < _count)
        {
            int slot = (start + length) % Capacity;
            ref uint word = ref _present[slot >> 5];
            uint bit = 1u << (slot & 31);
            if ((word & bit) == 0) { break; }
            word &= ~bit;
            length++;
        }
        _count -= length;
        var bytes = new byte[length];
        int beforeWrap = Math.Min(length, Capacity - start);
        _bytes.AsSpan(start, beforeWrap).CopyTo(bytes);
        _bytes.AsSpan(0, length - beforeWrap).CopyTo(bytes.AsSpan(beforeWrap));
        return bytes;
    }
}
