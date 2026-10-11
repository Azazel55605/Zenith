using System;
using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>IRQ-safe immutable queue snapshots. Drop new packets once the queue reaches 32.</summary>
public sealed class BoundedPacketQueue<T>
{
    public const int MaximumPackets = 32;
    private T[] _items = Array.Empty<T>();
    public BoundedPacketQueue(int initialCapacity) { }
    public int Count => Volatile.Read(ref _items).Length;
    public void Enqueue(T item)
    {
        while (true)
        {
            T[] before = Volatile.Read(ref _items);
            if (before.Length == MaximumPackets) { return; }
            T[] after = new T[before.Length + 1]; Array.Copy(before, after, before.Length); after[before.Length] = item;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, after, before), before)) { return; }
        }
    }
    public T Dequeue()
    {
        while (true)
        {
            T[] before = Volatile.Read(ref _items);
            if (before.Length == 0) { throw new InvalidOperationException("receive queue empty"); }
            T[] after = new T[before.Length - 1]; Array.Copy(before, 1, after, 0, after.Length);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, after, before), before)) { return before[0]; }
        }
    }
    public void Clear() => Interlocked.Exchange(ref _items, Array.Empty<T>());
}
