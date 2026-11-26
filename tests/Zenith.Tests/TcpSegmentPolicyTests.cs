using System;
using System.Linq;
using Zenith.Core.Network.Transport;
namespace Zenith.Tests;
public sealed class TcpSegmentPolicyTests
{
    [Fact]
    public void AdvertisedWindowClosesAndReopensWhenApplicationDrains()
    {
        var buffer = new BoundedTcpBuffer(); Assert.Equal(32768, buffer.Window);
        Assert.True(buffer.Append(new byte[32768])); Assert.Equal(0, buffer.Window);
        buffer.Advance(16384); Assert.Equal(16384, buffer.Window); buffer.Advance(16384); Assert.Equal(32768, buffer.Window);
    }
    [Fact]
    public void OverflowCannotAdvertiseFreshReceiveSpace()
    {
        var buffer = new BoundedTcpBuffer(); Assert.False(buffer.Append(new byte[BoundedTcpBuffer.Maximum + 1])); Assert.Equal(0, buffer.Window);
    }
    [Theory]
    [InlineData(100u,8192u,100u,1440u,true)]
    [InlineData(100u,8192u,100u,8192u,true)]
    [InlineData(100u,0u,100u,0u,true)]
    [InlineData(100u,8192u,101u,1440u,false)]
    [InlineData(100u,8192u,99u,1440u,false)]
    [InlineData(100u,8192u,100u,8193u,false)]
    [InlineData(uint.MaxValue-10,8192u,uint.MaxValue-10,1440u,true)]
    public void OnlyContiguousPayloadFitsAdvertisedWindow(uint next,uint window,uint sequence,uint bytes,bool expected)
        => Assert.Equal(expected,TcpSegmentPolicy.Accept(next,window,sequence,bytes));

    [Fact]
    public void RejectionsAreCountedOnceAcrossSequenceWrap()
    {
        var start = TcpSegmentPolicy.Counters();
        Assert.True(TcpSegmentPolicy.Accept(uint.MaxValue, 2, uint.MaxValue, 2));
        Assert.False(TcpSegmentPolicy.Accept(uint.MaxValue, 0, 1, 0));                 // ahead across wrap
        Assert.False(TcpSegmentPolicy.Accept(1, 3, uint.MaxValue - 1, 3));           // ends at expected
        Assert.False(TcpSegmentPolicy.Accept(1, 3, uint.MaxValue, 3));               // covers expected
        Assert.False(TcpSegmentPolicy.Accept(1, 0, 1, 1));                           // closed window
        Assert.False(TcpSegmentPolicy.Accept(1, 3, 0x80000001u, 0));                 // no serial ordering
        Assert.Equal(new TcpRejectCounts(1, 1, 1, 1, 1), TcpSegmentPolicy.Counters().Since(start));
    }

    [Theory]
    [InlineData(3, 0x10, true)]  // established ACK
    [InlineData(3, 0x18, true)]  // established ACK + PSH
    [InlineData(3, 0x11, false)] // FIN
    [InlineData(3, 0x12, false)] // SYN
    [InlineData(3, 0x14, false)] // RST
    [InlineData(3, 0x30, false)] // URG
    [InlineData(3, 0x08, false)] // missing ACK
    [InlineData(4, 0x10, false)] // FIN_WAIT_1
    public void OnlyEstablishedOrdinaryDataCanWaitBehindAGap(int state, int flags, bool allowed)
    {
        Assert.Equal(allowed, TcpSegmentPolicy.ReceiveAllowed(100, 4096, 101, 3, state, (byte)flags));
    }

    [Fact]
    public void PartialRetransmissionsAndDuplicateBytesReassembleOnceAcrossWrap()
    {
        uint next = uint.MaxValue - 3;
        var buffer = new BoundedTcpBuffer();
        var initial = TcpSegmentPolicy.Counters();
        Assert.Equal(0, buffer.Receive(next, unchecked(next + 4), "efgh"u8.ToArray(), false));
        Assert.Equal(0, buffer.Receive(next, unchecked(next + 6), "XXij"u8.ToArray(), false));
        Assert.Equal(0, buffer.Receive(next, unchecked(next + 4), "YYYY"u8.ToArray(), false));
        Assert.Empty(buffer.Data.ToArray());
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow, buffer.Window);
        // Retransmission starts in the already acknowledged prefix.
        Assert.Equal(10, buffer.Receive(next, unchecked(next - 2), "zzabcd"u8.ToArray(), false));
        Assert.Equal("abcdefghij"u8.ToArray(), buffer.Data.ToArray());
        Assert.Equal(0, buffer.Receive(unchecked(next + 10), next, "abcdefghij"u8.ToArray(), false));
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow - 10, buffer.Window);
        var counts = TcpSegmentPolicy.Counters().Since(initial);
        Assert.Equal(2, counts.Buffered);
        Assert.Equal(6, counts.RecoveredBytes);
        buffer.Advance(10);
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow, buffer.Window);
    }

    [Fact]
    public void SparseDataAtTheWindowEdgeNeverHidesTheMissingByte()
    {
        var buffer = new BoundedTcpBuffer();
        uint next = 13;
        var bytes = Enumerable.Range(0, BoundedTcpBuffer.ReceiveWindow).Select(i => (byte)i).ToArray();
        Assert.Equal(0, buffer.Receive(next, next + (uint)bytes.Length, new byte[] { 7 }, false));
        for (int i = 1; i < bytes.Length; i += 1024)
        {
            int length = System.Math.Min(1024, bytes.Length - i);
            Assert.Equal(0, buffer.Receive(next, next + (uint)i, bytes.AsSpan(i, length).ToArray(), false));
        }
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow, buffer.Window);
        Assert.Equal(bytes.Length, buffer.Receive(next, next, new byte[] { 0 }, false));
        Assert.Equal(0, buffer.Window);
        Assert.Equal(bytes, buffer.Data.ToArray());
        Assert.Equal(0, buffer.Receive(next + (uint)bytes.Length, next + (uint)bytes.Length, new byte[] { 1 }, false));
        buffer.Advance(1);
        Assert.Equal(1, buffer.Window);
    }

    [Fact]
    public void ReadingQueuedApplicationBytesReopensTheRightEdgeWithoutMovingTheGap()
    {
        var buffer = new BoundedTcpBuffer();
        Assert.Equal(4, buffer.Receive(20, 20, "head"u8.ToArray(), false));
        Assert.Equal(0, buffer.Receive(24, 28, "tail"u8.ToArray(), false));
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow - 4, buffer.Window);
        buffer.Advance(4);
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow, buffer.Window);
        Assert.Empty(buffer.Data.ToArray());
        Assert.Equal(8, buffer.Receive(24, 24, "body"u8.ToArray(), false));
        Assert.Equal("bodytail"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void FinPayloadStopsAtTheFinAndPreservesEarlierQueuedOverlap()
    {
        var buffer = new BoundedTcpBuffer();
        Assert.Equal(0, buffer.Receive(10, 12, "cdefg"u8.ToArray(), false));
        Assert.Equal(0, buffer.Receive(10, 11, "bc"u8.ToArray(), true));
        Assert.Equal(4, buffer.Receive(10, 10, "abXX"u8.ToArray(), true));
        Assert.Equal("abcd"u8.ToArray(), buffer.Data.ToArray());
    }

    [Fact]
    public void ClosingAConnectionDropsItsReorderState()
    {
        BoundedTcpBuffer? buffer = null;
        Assert.Equal(0, TcpReceiveBridge.Receive(ref buffer, 10, 12, "old"u8.ToArray(), false));
        TcpReceiveBridge.Release(ref buffer);
        Assert.Null(buffer);
        Assert.Equal(2, TcpReceiveBridge.Receive(ref buffer, 10, 10, "ab"u8.ToArray(), false));
        Assert.Equal("ab"u8.ToArray(), TcpReceiveBridge.Data(ref buffer).ToArray());
    }
    [Fact]
    public void ReopenedZeroWindowOneByteProbeThenOverlappingFullSegmentDeliversThePrefixOnce()
    {
        var buffer = new BoundedTcpBuffer();
        uint start = uint.MaxValue - 500; // also exercise sequence wrap during the original full window
        byte[] full = new byte[BoundedTcpBuffer.ReceiveWindow];
        Assert.Equal(full.Length, buffer.Receive(start, start, full, false));
        uint next = unchecked(start + (uint)full.Length);
        Assert.Equal(0, buffer.Window);
        Assert.False(TcpSegmentPolicy.ReceiveAllowed(next, buffer.Window, next, 1, 3, 0x10));
        buffer.Advance(full.Length);
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow, buffer.Window);

        var bytes = new byte[2920];
        new Random(4321).NextBytes(bytes);
        Assert.Equal(1, buffer.Receive(next, next, new[] { bytes[0] }, false));
        Assert.True(TcpSegmentPolicy.ReceiveAllowed(next + 1, buffer.Window, next, 1460, 3, 0x10));
        Assert.Equal(1459, buffer.Receive(next + 1, next, bytes[..1460], false));
        Assert.Equal(1460, buffer.Receive(next + 1460, next + 1460, bytes[1460..], false));
        Assert.Equal(bytes, buffer.Data.ToArray());
        Assert.Equal(BoundedTcpBuffer.ReceiveWindow - bytes.Length, buffer.Window);
    }
}
