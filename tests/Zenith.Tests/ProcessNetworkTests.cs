using System;
using System.Buffers.Binary;
using System.IO;
using Zenith.Core.Processes;

namespace Zenith.Tests;

public sealed class ProcessNetworkTests
{
    private sealed class Connection : IProcessConnection
    {
        public bool Closed;
        public bool BadRead, BadWrite;
        public int Read(byte[] buffer) { if (BadRead) { return buffer.Length + 1; } buffer.AsSpan().Fill(42); return buffer.Length; }
        public int Write(byte[] buffer) => BadWrite ? -1 : buffer.Length;
        public void Dispose() => Closed = true;
    }
    private sealed class Sink : IProcessDownload
    {
        public bool Closed, Committed, FailCommit;
        public int Bytes;
        public int Write(byte[] buffer) { Bytes += buffer.Length; return buffer.Length; }
        public void Commit(int expected) { if (FailCommit || expected != Bytes) { throw new IOException("mismatch"); } Committed = true; }
        public void Dispose() => Closed = true;
    }
    private sealed class Source : IProcessNetworkSource
    {
        public Connection Connection = new();
        public Sink Sink = new();
        public int Opens;
        public string Status() => "network snapshot";
        public string Resolve(string host, string? server, int port) => "10.0.2.2";
        public long Echo(string host) => 7;
        public IProcessConnection Connect(string host, int port, bool secure) { Opens++; return Connection = new(); }
        public IProcessDownload Download(string path, bool replace, int maximum) => Sink = new();
    }
    [Fact]
    public void HandlesBelongToAnInvocationAndNeverReuseAfterClose()
    {
        var firstSource = new Source(); var otherSource = new Source();
        using var first = new ProcessNetwork(firstSource); using var other = new ProcessNetwork(otherSource);
        int handle = first.Connect("host", 443, true);
        Assert.Equal(-9, other.Read(handle, new byte[10]));
        Assert.Equal(-9, other.Write(handle, new byte[10]));
        Assert.Equal(-9, other.Close(handle));
        Assert.Equal(-16, first.Connect("host", 443, true));
        Assert.Equal(0, first.Close(handle)); Assert.True(firstSource.Connection.Closed);
        int next = first.Connect("host", 80, false); Assert.True(next > handle);
        Assert.Equal(-9, first.Read(handle, new byte[10]));
        first.Close(next); Assert.Equal(-28, first.Connect("host", 80, false));
    }
    [Fact]
    public void TeardownReleasesConnectionAndAbortsUncommittedDownload()
    {
        var source = new Source(); var network = new ProcessNetwork(source);
        network.Connect("host", 443, true); network.Download("/home/user/a", false, 1024);
        Assert.Equal(-16, network.Download("/home/user/b", false, 1024));
        network.Dispose(); network.Dispose();
        Assert.True(source.Connection.Closed); Assert.True(source.Sink.Closed); Assert.False(source.Sink.Committed);
        Assert.Equal(-9, network.Connect("host", 80, false));
    }
    [Fact]
    public void CommitFailureRetainsAbortableHandleAndSuccessRetiresIt()
    {
        var source = new Source(); using var network = new ProcessNetwork(source);
        int handle = network.Download("/home/user/a", false, 1024);
        Assert.Equal(-9, network.Read(handle, new byte[1]));
        network.Write(handle, new byte[4]); source.Sink.FailCommit = true;
        Assert.Throws<IOException>(() => network.Commit(handle, 4));
        source.Sink.FailCommit = false; Assert.Equal(0, network.Commit(handle, 4));
        Assert.True(source.Sink.Committed); Assert.True(source.Sink.Closed);
        Assert.Equal(-9, network.Commit(handle, 4)); Assert.Equal(-9, network.Close(handle));
    }
    [Fact]
    public void BoundsAndProviderCountsAreCheckedBeforeAccounting()
    {
        var source = new Source(); using var network = new ProcessNetwork(source);
        Assert.Equal(-22, network.Connect("host", 0, false)); Assert.Equal(0, source.Opens);
        int handle = network.Connect("host", 80, false);
        Assert.Equal(-22, network.Read(handle, new byte[65537]));
        Assert.Equal(-22, network.Write(handle, new byte[65537]));
        Assert.Equal(65536, network.Write(handle, new byte[65536])); Assert.Equal(-28, network.Write(handle, new byte[1]));
        source.Connection.BadRead = true; Assert.Throws<IOException>(() => network.Read(handle, new byte[10]));
        Assert.Equal(-22, network.Download("a", false, ProcessNetwork.MaximumTransfer + 1));
    }
    [Fact]
    public void PartialReadsAtTheAggregateLimitAndFailedWritesRemainBounded()
    {
        var source = new Source(); using var network = new ProcessNetwork(source);
        int handle = network.Connect("host", 443, true);
        byte[] block = new byte[65536]; int remaining = 2 * (ProcessNetwork.MaximumTransfer + 8192);
        while (remaining >= block.Length) { Assert.Equal(block.Length, network.Read(handle, block)); remaining -= block.Length; }
        Assert.Equal(remaining, network.Read(handle, block));
        Assert.Equal(-28, network.Read(handle, new byte[1]));
        source.Connection.BadWrite = true; Assert.Throws<IOException>(() => network.Write(handle, new byte[1]));
    }
    [Fact]
    public void MetadataCallsAndDiagnosticTextAreBounded()
    {
        using var network = new ProcessNetwork(new Source());
        Assert.Equal("10.0.2.2", network.Resolve("host", null, 53)); Assert.Equal(7, network.Echo("host"));
        for (int i = 2; i < 32; i++) { Assert.Equal("network snapshot", network.Status()); }
        Assert.Throws<IOException>(() => network.Status());
        network.Error(new string('x', 1024)); Assert.Equal(512, network.LastError.Length);
    }
    private static byte[] Request(uint operation, uint flags = 0, ulong input = 0, ulong length = 0,
        ulong target = 0, ulong targetLength = 0, ulong output = 0, ulong capacity = 0, ulong value = 0, ulong extra = 0)
    {
        byte[] bytes = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, operation); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), flags);
        ulong[] fields = { input, length, target, targetLength, output, capacity, value, extra };
        for (int i = 0; i < fields.Length; i++) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8 + i * 8), fields[i]); }
        return bytes;
    }
    [Fact]
    public void ControlRequestRejectsUnknownFlagsUnusedPointersAndOversizedFields()
    {
        Assert.True(ProcessNetworkRequest.TryRead(Request(1, output: 0x400000, capacity: 4096), out _));
        Assert.True(ProcessNetworkRequest.TryRead(Request(2, input: 0x400000, length: 4, output: 0x401000, capacity: 16, value: 53), out _));
        Assert.True(ProcessNetworkRequest.TryRead(Request(4, flags: 1, input: 0x400000, length: 4, value: 443), out _));
        Assert.True(ProcessNetworkRequest.TryRead(Request(7, value: 10000, extra: 65536), out _));
        foreach (byte[] malformed in new[] {
            Request(0), Request(8), Request(1, output: 1, capacity: 4097), Request(1, flags: 1, output: 1, capacity: 16),
            Request(4, input: 1, length: 254, value: 443), Request(4, input: 1, length: 1, value: 65536),
            Request(3, input: 1, length: 1, output: 1, capacity: 16), Request(6, input: 1, length: 1, output: 1, capacity: 16),
            Request(2, input: 1, length: 1, target: 1, output: 1, capacity: 16, value: 53),
            Request(7, value: 9999), Request(5, input: 1, length: 1, value: ulong.MaxValue), new byte[79] })
        { Assert.False(ProcessNetworkRequest.TryRead(malformed, out _)); }
        byte[] reserved = Request(1, output: 1, capacity: 16); reserved[72] = 1;
        Assert.False(ProcessNetworkRequest.TryRead(reserved, out _));
    }
}
