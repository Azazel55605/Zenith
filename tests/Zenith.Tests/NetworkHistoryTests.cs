using System;
using System.Buffers.Binary;
using System.IO;
using Zenith.Core.Network;
using Zenith.Core.Portal;
using Zenith.Core.Processes;

namespace Zenith.Tests;

/// <summary>Beacon's history (kernel 0.0.69): traffic at four resolutions, bounded events and the on-disk format.</summary>
public class NetworkHistoryTests
{
    private static (uint Count, uint Step, uint[] In, uint[] Out) Decode(byte[] b)
    {
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)), step = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        var i = new uint[n]; var o = new uint[n];
        for (int k = 0; k < n; k++) { i[k] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16 + k * 8)); o[k] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20 + k * 8)); }
        return (n, step, i, o);
    }

    [Fact]
    public void SecondsAverageIntoMinutesQuarterHoursAndHours()
    {
        var h = new NetworkHistory();
        for (int s = 0; s < 3600; s++) { h.Sample((uint)(s < 1800 ? 1000 : 3000), 10); }
        var minute = Decode(h.Encode(0)); var hour = Decode(h.Encode(1)); var day = Decode(h.Encode(2)); var week = Decode(h.Encode(3));
        Assert.Equal((60u, 1u), (minute.Count, minute.Step));       // the ring keeps the last minute only
        Assert.All(minute.In, v => Assert.Equal(3000u, v));
        Assert.Equal((60u, 60u), (hour.Count, hour.Step));
        Assert.Equal(1000u, hour.In[0]); Assert.Equal(3000u, hour.In[59]);
        Assert.Equal((4u, 900u), (day.Count, day.Step));
        Assert.Equal(new uint[] { 1000, 1000, 3000, 3000 }, day.In);
        Assert.Equal((1u, 3600u), (week.Count, week.Step));
        Assert.Equal(2000u, week.In[0]); Assert.Equal(10u, week.Out[0]);
        Assert.Throws<ArgumentException>(() => h.Encode(4));
    }

    [Fact]
    public void EventsAreBoundedSanitizedAndNewestFirst()
    {
        var h = new NetworkHistory();
        for (int i = 0; i < 70; i++) { h.Record(1000 + i, NetworkEventKind.Renewed, i, "Lease renewed " + i); }
        h.Record(2000, NetworkEventKind.Failed, 0, "Bad\nlineé " + new string('x', 80));
        byte[] e = h.EncodeEvents();
        Assert.Equal((uint)NetworkHistory.MaximumEvents, BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(4)));
        Assert.Equal(71u, BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(8)));
        Assert.Equal(2000, BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(16)));
        string text = System.Text.Encoding.ASCII.GetString(e, 16 + 16, 48).TrimEnd('\0');
        Assert.StartsWith("Bad?line? xx", text); Assert.True(text.Length <= NetworkHistory.EventText);
        Assert.Equal(1069, BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(16 + 64)));   // then the newest renewal
    }

    [Fact]
    public void DayWeekAndEventsSurviveARestartThroughTheFile()
    {
        var h = new NetworkHistory();
        for (int s = 0; s < 3600; s++) { h.Sample(500, 50); }
        h.Record(42, NetworkEventKind.CableOut, 0, "Cable unplugged");
        Assert.True(h.Dirty);
        string text = h.Serialize(); Assert.False(h.Dirty);
        Assert.True(text.Length < 65536);
        var again = new NetworkHistory(); again.Parse(text + "S 9 1 1\nE x 1 0 bad\nE 5 99 0 unknown kind\ngarbage\n");
        Assert.Equal(4u, Decode(again.Encode(2)).Count); Assert.Equal(1u, Decode(again.Encode(3)).Count);
        Assert.Equal(0u, Decode(again.Encode(0)).Count);   // the minute and hour are not kept across restarts
        Assert.Equal(1u, again.EventTotal);
        Assert.Equal(NetworkEventKind.CableOut, again.Last!.Value.Kind);
        // A full week and 64 events stay within a fixed size on disk.
        var full = new NetworkHistory();
        for (int s = 0; s < 3600 * 170; s += 1) { full.Sample(uint.MaxValue, uint.MaxValue); }
        for (int i = 0; i < 100; i++) { full.Record(long.MaxValue, NetworkEventKind.SettingsKept, int.MinValue, new string('w', 100)); }
        Assert.True(full.Serialize().Length < 16384);
    }

    private sealed class Quiet : IProcessNetworkSource
    {
        public string Status() => ""; public string Resolve(string host, string? server, int port) => "10.0.2.2"; public long Echo(string host) => 1;
        public IProcessConnection Connect(string host, int port, bool secure) => new Link();
        public IProcessDownload Download(string path, bool replace, int maximum) => throw new IOException();
        public void ThrowIfCancelled() { }
        private sealed class Link : IProcessConnection { public int Read(byte[] b) => 5; public int Write(byte[] b) => b.Length; public void Dispose() { } }
    }

    [Fact]
    public void ProgramsUsingTheNetworkAreListedUntilTheyExit()
    {
        var idle = new ProcessNetwork(new Quiet(), 7001); var busy = new ProcessNetwork(new Quiet(), 7002);
        int handle = busy.Connect("mirror.example", 443, true);
        busy.Read(handle, new byte[64]); busy.Write(handle, new byte[10]);
        var list = ProcessNetwork.Programs();
        Assert.DoesNotContain(list, u => u.Pid == 7001);   // never used the network
        var usage = Assert.Single(list, u => u.Pid == 7002);
        Assert.Equal((1, 5L, 10L, "mirror.example:443 TLS"), (usage.Connections, usage.Received, usage.Sent, usage.Peer));
        busy.Close(handle);
        Assert.Equal("", Assert.Single(ProcessNetwork.Programs(), u => u.Pid == 7002).Peer);
        busy.Dispose(); idle.Dispose();
        Assert.DoesNotContain(ProcessNetwork.Programs(), u => u.Pid is 7001 or 7002);
    }

    [Fact]
    public void NetworkChangesNeedTheNetworkGrant()
    {
        Assert.Equal(Privilege.Network, ProcessPrivileges.For(667, 0));
        ProcessPrivileges.Grant(91001, Privilege.SystemSettings | Privilege.Network);
        Assert.True(ProcessPrivileges.Elevates(91001, 667, 0));
        ProcessPrivileges.Drop(91001);
        Assert.False(ProcessPrivileges.Elevates(91001, 667, 0));
    }
}
