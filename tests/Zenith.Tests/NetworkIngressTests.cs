using System;
using System.Threading.Tasks;
using Zenith.Core.Network.Transport;

namespace Zenith.Tests;

public sealed class NetworkIngressTests
{
    private static byte[] Frame(byte protocol, int payload)
    {
        byte[] b = new byte[34 + payload]; b[12] = 8; b[14] = 0x45;
        int total = 20 + payload; b[16] = (byte)(total >> 8); b[17] = (byte)total;
        b[22] = 64; b[23] = protocol; b[26] = 10; b[29] = 2; b[30] = 10; b[33] = 15;
        FixChecksum(b); return b;
    }
    private static void FixChecksum(byte[] b)
    {
        b[24] = b[25] = 0; uint sum = 0;
        for (int p = 14; p < 34; p += 2) { sum += (uint)((b[p] << 8) | b[p + 1]); }
        while (sum >> 16 != 0) { sum = (sum & 65535) + (sum >> 16); }
        ushort checksum = (ushort)~sum; b[24] = (byte)(checksum >> 8); b[25] = (byte)checksum;
    }
    [Fact]
    public void RejectsTruncationBadLengthsFragmentsAndChecksumBeforeFrameworkParser()
    {
        byte[] udp = Frame(17, 8); udp[39] = 8;
        Assert.True(EthernetGuard.Accept(udp, udp.Length));
        for (int length = 0; length < udp.Length; length++) { Assert.False(EthernetGuard.Accept(udp, length)); }
        Assert.False(EthernetGuard.Accept(udp, udp.Length + 1));
        Assert.False(EthernetGuard.Accept(null, 40));
        udp[39] = 7; Assert.False(EthernetGuard.Accept(udp, udp.Length)); udp[39] = 8;
        udp[20] = 0x20; FixChecksum(udp); Assert.False(EthernetGuard.Accept(udp, udp.Length));
        udp[20] = 0; FixChecksum(udp); udp[24] ^= 1; Assert.False(EthernetGuard.Accept(udp, udp.Length));
    }
    [Fact]
    public void ValidatesTcpOptionsAndArpShape()
    {
        byte[] tcp = Frame(6, 24); tcp[46] = 0x60; tcp[54] = 2; tcp[55] = 4;
        Assert.True(EthernetGuard.Accept(tcp, tcp.Length));
        tcp[55] = 20; Assert.False(EthernetGuard.Accept(tcp, tcp.Length));
        tcp[54] = 0; Assert.True(EthernetGuard.Accept(tcp, tcp.Length)); // EOL padding is valid
        tcp[46] = 0x70; Assert.False(EthernetGuard.Accept(tcp, tcp.Length));
        byte[] arp = new byte[42]; arp[12] = 8; arp[13] = 6; arp[15] = 1; arp[16] = 8;
        arp[18] = 6; arp[19] = 4; arp[21] = 1;
        Assert.True(EthernetGuard.Accept(arp, arp.Length));
        arp[18] = 255; Assert.False(EthernetGuard.Accept(arp, arp.Length));
    }
    [Fact]
    public void QueueDropsFloodWithoutCorruptingPriorPacketsAndCanBeReused()
    {
        var queue = new BoundedPacketQueue<int>(8);
        for (int i = 0; i < 10000; i++) { queue.Enqueue(i); }
        Assert.Equal(32, queue.Count);
        for (int i = 0; i < 32; i++) { Assert.Equal(i, queue.Dequeue()); }
        Assert.Throws<InvalidOperationException>(() => queue.Dequeue());
        queue.Enqueue(10); queue.Clear(); Assert.Equal(0, queue.Count);
        queue.Enqueue(11); Assert.Equal(11, queue.Dequeue());
    }
    [Fact]
    public async Task QueueConcurrentProducersStayBoundedAndRetainUniqueItems()
    {
        var queue = new BoundedPacketQueue<int>(8);
        await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 10000; i++) queue.Enqueue(i); }),
            Task.Run(() => { for (int i = 10000; i < 20000; i++) queue.Enqueue(i); }));
        Assert.Equal(32, queue.Count);
        var seen = new System.Collections.Generic.HashSet<int>();
        while (queue.Count > 0) { Assert.True(seen.Add(queue.Dequeue())); }
        Assert.Equal(32, seen.Count);
    }
}
