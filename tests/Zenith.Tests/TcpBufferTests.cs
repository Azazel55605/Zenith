using System;
using System.Threading.Tasks;
using Zenith.Core.Network.Transport;

namespace Zenith.Tests;

public sealed class TcpBufferTests
{
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
            while (read < count)
            {
                var snapshot = buffer.Data;
                if (snapshot.Length == 0) { System.Threading.Thread.Yield(); continue; }
                int n = Math.Min(snapshot.Length, 17);
                for (int i = 0; i < n; i++) { Assert.Equal((byte)(read + i), snapshot[i]); }
                buffer.Advance(n); read += n;
            }
        });
        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(15)); Assert.Empty(buffer.Data.ToArray());
    }
}
