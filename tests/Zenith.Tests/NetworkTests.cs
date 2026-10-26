using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zenith.Core.Network;

namespace Zenith.Tests;

public sealed class NetworkTests
{
    [Fact]
    public void MissingOptionalIndexKeepsIts404InsteadOfRetryingTransport()
    {
        var parser = new HttpDownload((_, _, _) => throw new Exception("error body must not commit"), () => { });
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.0 404 Not Found\r\nContent-Length: 3\r\n\r\nbad");
        var missing = Assert.Throws<IOException>(() => parser.Feed(response, response.Length));
        Assert.Equal("HTTP status 404", missing.Message);
        Assert.False(HttpDownload.RetryGet(missing, 0));
        Assert.True(HttpDownload.RetryGet(new IOException("TLS transport closed unexpectedly"), 0));
        Assert.False(HttpDownload.RetryGet(new IOException("TLS transport closed unexpectedly"), 1));
        Assert.False(HttpDownload.RetryGet(new OperationCanceledException(), 0));
        Assert.False(HttpDownload.RetryGet(new ArgumentException(), 0));
    }
    [Fact]
    public void KernelTransferContinuesPastOldLimitsButRemainsBounded()
    {
        HttpDownload.CheckTimeout(10000, 300000, 10000000, 16000000, HttpDownload.MaximumBytes);
        var idle = Assert.Throws<IOException>(() => HttpDownload.CheckTimeout(30001, 300000, 10000000, 16000000, HttpDownload.MaximumBytes));
        Assert.Contains("idle timeout", idle.Message); Assert.Contains("received=10000000/16000000", idle.Message);
        var total = Assert.Throws<IOException>(() => HttpDownload.CheckTimeout(0, 900001, 10000000, 16000000, HttpDownload.MaximumBytes));
        Assert.Contains("transfer limit", total.Message);
        Assert.Throws<IOException>(() => HttpDownload.CheckTimeout(3001, 10000, 1, 2, HttpDownload.DefaultMaximumBytes));
        Assert.Throws<IOException>(() => HttpDownload.CheckTimeout(0, 30001, 1, 2, HttpDownload.DefaultMaximumBytes));
    }
    [Theory]
    [InlineData("10.0.2.15", 0x0a00020fU)]
    [InlineData("0.0.0.0", 0U)]
    [InlineData("255.255.255.255", uint.MaxValue)]
    public void AddressesRoundTrip(string address, uint packed)
    { Assert.Equal(packed, NetworkText.Ip(address)); Assert.Equal(address, NetworkText.Format(packed)); }
    [Theory]
    [InlineData("1.2.3")][InlineData("1.2.3.256")][InlineData(" 1.2.3.4")][InlineData("1.2.3.-1")][InlineData("1.2.3.+1")][InlineData("a.b.c.d")]
    public void InvalidAddressRejected(string address) => Assert.Throws<IOException>(() => NetworkText.Ip(address));
    [Fact]
    public void ConfigRoundTripsAndRejectsFutureDuplicateAndInvalidStatic()
    {
        var config = new NetworkConfiguration("static", "10.0.2.15", "255.255.255.0", "10.0.2.2", "10.0.2.3", "time.home", 1234);
        config.Validate(); Assert.Equal(config, NetworkConfiguration.Parse(config.Serialize()));
        Assert.Throws<IOException>(() => NetworkConfiguration.Parse("VERSION=2\nMODE=dhcp\n"));
        Assert.Throws<IOException>(() => NetworkConfiguration.Parse("VERSION=1\nMODE=dhcp\nMODE=off\n"));
        Assert.Throws<IOException>(() => (config with { Gateway = "192.168.1.1" }).Validate());
        Assert.Throws<IOException>(() => (config with { Mask = "255.0.255.0" }).Validate());
        Assert.Throws<IOException>(() => (config with { Address = "10.0.2.255" }).Validate());
        Assert.Throws<IOException>(() => (config with { Address = "127.0.0.1" }).Validate());
        Assert.Throws<IOException>(() => (config with { Dns = "255.255.255.255" }).Validate());
    }
    [Fact]
    public void LocalNameFilesBoundedAndCommentsAliasesHandled()
    {
        Assert.Equal("10.0.2.2", NetworkText.LookupHosts("# comment\n10.0.2.2 mirror.home Mirror # aliases\n", "MIRROR"));
        Assert.Equal("10.0.2.2", NetworkText.LookupHosts("010.000.002.002 mirror", "mirror"));
        Assert.Equal(new[] { "10.0.2.2" }, NetworkText.Resolvers("nameserver 010.000.002.002"));
        Assert.Null(NetworkText.LookupHosts("10.0.2.2 other", "missing"));
        Assert.Equal(new[] { "10.0.2.3" }, NetworkText.Resolvers("# dynamic default\nnameserver 10.0.2.3 # local\n"));
        Assert.Throws<IOException>(() => NetworkText.Resolvers("nameserver invalid"));
        Assert.Throws<IOException>(() => NetworkText.LookupHosts(new string('x', 16385), "host"));
    }
    private static byte[] Dhcp()
    {
        byte[] p = DhcpWire.Request(123, new byte[6]); p[0] = 2; NetworkWire.Put32(p, 16, 0x0a00020f);
        int i = 240;
        void Option(byte type, uint value) { p[i++] = type; p[i++] = 4; NetworkWire.Put32(p, i, value); i += 4; }
        Option(1, 0xffffff00); Option(3, 0x0a000202); Option(6, 0x0a000203); Option(54, 0x0a000202); Option(51, 3600);
        p[i++] = 53; p[i++] = 1; p[i++] = 5; p[i] = 255; return p;
    }
    [Fact]
    public void DhcpMatchesTransactionMacAndAckIndependentOfOptionOrder()
    {
        var p = Dhcp(); var lease = DhcpWire.Parse(p, 123, new byte[6], 5);
        Assert.Equal(0x0a00020fU, lease.Address); Assert.Equal(3600U, lease.Seconds);
        Assert.Throws<IOException>(() => DhcpWire.Parse(p, 124, new byte[6], 5));
        Assert.Throws<IOException>(() => DhcpWire.Parse(p, 123, new byte[] { 1, 0, 0, 0, 0, 0 }, 5));
        Assert.Throws<IOException>(() => DhcpWire.Parse(p, 123, new byte[6], 2));
        for (int n = 0; n < 274; n++) { byte[] shortened = p.AsSpan(0, n).ToArray(); Assert.Throws<IOException>(() => DhcpWire.Parse(shortened, 123, new byte[6], 5)); }
    }
    [Fact]
    public void VMwareOfferProfileIsAccepted()
    {
        byte[] mac = { 0x00, 0x0c, 0x29, 0x55, 0xc5, 0x54 };
        var p = DhcpWire.Request(0xf4022d6f, mac); p[0] = 2;
        NetworkWire.Put32(p, 16, 0xac102b8d); NetworkWire.Put32(p, 20, 0xac102bfe);
        int i = 240; p[i++] = 53; p[i++] = 1; p[i++] = 2;
        void Option(byte type, uint value) { p[i++] = type; p[i++] = 4; NetworkWire.Put32(p, i, value); i += 4; }
        Option(54, 0xac102bfe); Option(51, 1800); Option(1, 0xffffff00);
        Option(3, 0xac102b02); Option(6, 0xac102b02); p[i] = 255;
        var lease = DhcpWire.Parse(p, 0xf4022d6f, mac, 2);
        Assert.Equal(0xac102b8dU, lease.Address); Assert.Equal(0xac102bfeU, lease.Server);
        Assert.Equal(0xac102b02U, lease.Gateway); Assert.Equal(0xac102b02U, lease.Dns);
        Assert.Equal(1800U, lease.Seconds);
    }
    [Fact]
    public void DhcpDoesNotAcceptNakOrDuplicateOptions()
    {
        var p = Dhcp(); p[272] = 6; Assert.Throws<IOException>(() => DhcpWire.Parse(p, 123, new byte[6], 5));
        p = Dhcp(); p[240] = 54; Assert.Throws<IOException>(() => DhcpWire.Parse(p, 123, new byte[6], 5));
    }
    private static byte[] Dns()
    {
        byte[] query = DnsWire.Query("mirror.home", 123); var p = new byte[query.Length + 16]; query.CopyTo(p, 0);
        NetworkWire.Put16(p, 2, 0x8580); NetworkWire.Put16(p, 6, 1); int i = query.Length;
        p[i++] = 0xc0; p[i++] = 12; NetworkWire.Put16(p, i, 1); NetworkWire.Put16(p, i + 2, 1);
        NetworkWire.Put32(p, i + 4, 60); NetworkWire.Put16(p, i + 8, 4); NetworkWire.Put32(p, i + 10, 0x0a000202); return p;
    }
    [Fact]
    public void DnsCorrelatesQuestionAndIdAndRejectsMalformedCompression()
    {
        var p = Dns(); Assert.Equal("10.0.2.2", DnsWire.Parse(p, "mirror.home", 123));
        Assert.Throws<IOException>(() => DnsWire.Parse(p, "other.home", 123));
        Assert.Throws<IOException>(() => DnsWire.Parse(p, "mirror.home", 124));
        for (int n = 0; n < p.Length; n++) { byte[] shortened = p.AsSpan(0, n).ToArray(); Assert.Throws<IOException>(() => DnsWire.Parse(shortened, "mirror.home", 123)); }
        p[12] = 0xc0; p[13] = 12; Assert.Throws<IOException>(() => DnsWire.Parse(p, "mirror.home", 123));
        p = Dns(); p[3] |= 3; Assert.Throws<IOException>(() => DnsWire.Parse(p, "mirror.home", 123));
        p = Dns(); p[2] |= 2; Assert.Throws<IOException>(() => DnsWire.Parse(p, "mirror.home", 123));
    }
    [Fact]
    public void NtpChecksOriginAndSynchronizationAndEra()
    {
        byte[] nonce = { 1, 2, 3, 4, 5, 6, 7, 8 }; byte[] p = new byte[48]; p[0] = 0x24; p[1] = 2; nonce.CopyTo(p, 24);
        var date = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        NetworkWire.Put32(p, 40, (uint)(date - new DateTime(1900, 1, 1)).TotalSeconds);
        Assert.Equal(date, NtpWire.Parse(p, nonce));
        p[24]++; Assert.Throws<IOException>(() => NtpWire.Parse(p, nonce)); p[24]--; p[1] = 0;
        Assert.Throws<IOException>(() => NtpWire.Parse(p, nonce)); p[1] = 2; p[0] |= 0xc0;
        Assert.Throws<IOException>(() => NtpWire.Parse(p, nonce)); p[0] = 0x24;
        date = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NetworkWire.Put32(p, 40, unchecked((uint)(ulong)(date - new DateTime(1900, 1, 1)).TotalSeconds));
        Assert.Equal(date, NtpWire.Parse(p, nonce));
    }
    [Fact]
    public void HttpStreamsAcrossEveryHeaderBoundaryAndChecksExactBody()
    {
        byte[] p = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Length: 5\r\n\r\nhello");
        for (int split = 0; split <= p.Length; split++)
        {
            using var output = new MemoryStream(); var parser = new HttpDownload((b, o, n) => output.Write(b, o, n), () => { });
            parser.Feed(p.AsSpan(0, split).ToArray(), split); parser.Feed(p.AsSpan(split).ToArray(), p.Length - split); parser.Complete();
            Assert.Equal("hello", Encoding.ASCII.GetString(output.ToArray()));
        }
    }
    [Theory]
    [InlineData("Content-Length: 5\r\nContent-Length: 5")]
    [InlineData("Content-Length: +5")][InlineData("Content-Length: 8388609")]
    [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 5")]
    [InlineData("Content-Encoding: gzip\r\nContent-Length: 5")]
    [InlineData("X-Test: missing length")]
    public void HttpRejectsAmbiguousOrUnsupportedFraming(string headers)
    {
        var parser = new HttpDownload((_, _, _) => { }, () => { }); byte[] p = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + headers + "\r\n\r\n");
        Assert.Throws<IOException>(() => parser.Feed(p, p.Length));
    }
    [Fact]
    public void HttpRejectsTruncationExcessHeadersAndHonorsCancellation()
    {
        var parser = new HttpDownload((_, _, _) => { }, () => { }); byte[] p = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Length: 5\r\n\r\nhey");
        parser.Feed(p, p.Length); Assert.Throws<IOException>(() => parser.Complete());
        byte[] extra = Encoding.ASCII.GetBytes("more"); Assert.Throws<IOException>(() => parser.Feed(extra, extra.Length));
        parser = new HttpDownload((_, _, _) => { }, () => { }); Assert.Throws<IOException>(() => parser.Feed(Encoding.ASCII.GetBytes(new string('x', 8193)), 8193));
        parser = new HttpDownload((_, _, _) => { }, () => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => parser.Feed(p, p.Length));
    }
    [Fact]
    public void HttpsUsesVerifiedTransportAndDefaultPort()
    {
        var target = HttpTarget.Parse("https://mirror.home/file");
        Assert.True(target.Secure); Assert.Equal(443, target.Port);
        Assert.False(HttpTarget.Parse("http://mirror.home/file").Secure);
        Assert.Equal(8443, HttpTarget.Parse("https://mirror.home:8443/file").Port);
    }
    [Theory]
    [InlineData("ftp://host/file")][InlineData("http://host:0/file")][InlineData("http://host/file\r\nInjected")]
    [InlineData("http://user@host/file")][InlineData("http://host/file#fragment")]
    public void HttpTargetRejectsUnsupportedAndInjectedUrls(string url) => Assert.ThrowsAny<Exception>(() => HttpTarget.Parse(url));
}
