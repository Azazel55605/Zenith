using System;
using System.Threading.Tasks;
using Zenith.Core.Network.Transport;

namespace Zenith.Tests;

public sealed class TcpBufferTests
{
    [Fact]
    public void CountingAndCopyingFromAPartiallyDrainedWindowAllocateNoSnapshot()
    {
        var buffer = new BoundedTcpBuffer();
        var packet = new byte[1440];
        for (int i = 0; i < 20; i++)
        {
            Array.Fill(packet, (byte)i);
            Assert.True(buffer.Append(packet));
        }
        buffer.Advance(500);
        var destination = new byte[1500];
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = buffer.Count;
        buffer.CopyPrefix(destination);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(20 * 1440 - 500, count);
        Assert.Equal(new byte[940], destination[..940]);
        Assert.All(destination[940..], value => Assert.Equal((byte)1, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CopyPrefix(new byte[count + 1]));
    }

    [Fact]
    public void PartialChunkDrainAppendAndHeldSnapshotsPreserveAllBytes()
    {
        var buffer = new BoundedTcpBuffer(); var first = new byte[10000];
        for (int i = 0; i < first.Length; i++) { first[i] = (byte)i; }
        Assert.True(buffer.Append(first)); var held = buffer.Data;
        buffer.Advance(4500); Assert.True(buffer.Append(new byte[] { 7, 8, 9 }));
        Assert.Equal(first, held.ToArray());
        Assert.Equal(first.AsSpan(4500).ToArray(), buffer.Data.Slice(0, 5500).ToArray());
        Assert.Equal(new byte[] { 7, 8, 9 }, buffer.Data.Slice(5500).ToArray());
        buffer.Advance(5503); Assert.Empty(buffer.Data.ToArray());
        Assert.True(buffer.Append(new byte[] { 1 })); Assert.Equal(new byte[] { 1 }, buffer.Data.ToArray());
    }
    [Fact]
    public void PartialConsumptionThenAppendPreservesUnreadBytesAndSnapshot()
    {
        var buffer = new BoundedTcpBuffer(); Assert.True(buffer.Append(new byte[] { 1, 2, 3, 4 }));
        var snapshot = buffer.Data; buffer.Advance(3); Assert.True(buffer.Append(new byte[] { 5, 6 }));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, snapshot.ToArray()); Assert.Equal(new byte[] { 4, 5, 6 }, buffer.Data.ToArray());
        buffer.Advance(3); Assert.Empty(buffer.Data.ToArray()); Assert.True(buffer.Append(new byte[] { 7 }));
    }
    [Fact]
    public void OverflowRetiresConnectionWithoutInvalidatingPreviousSnapshot()
    {
        var buffer = new BoundedTcpBuffer(); Assert.True(buffer.Append(new byte[BoundedTcpBuffer.Maximum]));
        var snapshot = buffer.Data; Assert.False(buffer.Append(new byte[1])); Assert.Empty(buffer.Data.ToArray());
        Assert.Equal(BoundedTcpBuffer.Maximum, snapshot.Length); buffer.Advance(123); Assert.False(buffer.Append(new byte[1]));
    }
    [Fact]
    public async Task ConcurrentProducerConsumerDoNotLoseReorderOrDuplicateBytes()
    {
        var buffer = new BoundedTcpBuffer(); const int count = 30000;
        var producer = Task.Run(() => { for (int i = 0; i < count; i++) { Assert.True(buffer.Append(new[] { (byte)i })); } });
        var consumer = Task.Run(() =>
        {
            int read = 0;
            var destination = new byte[17];
            while (read < count)
            {
                int n = Math.Min(buffer.Count, destination.Length);
                if (n == 0) { System.Threading.Thread.Yield(); continue; }
                buffer.CopyPrefix(destination.AsSpan(0, n));
                for (int i = 0; i < n; i++) { Assert.Equal((byte)(read + i), destination[i]); }
                buffer.Advance(n); read += n;
            }
        });
        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Empty(buffer.Data.ToArray());
    }
}
