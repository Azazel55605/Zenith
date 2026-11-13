using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Zenith.Core.Monitoring;
using Xunit;

namespace Zenith.Tests;

public class ActivityMonitorTests
{
    private sealed class Fake : IActivitySources
    {
        public long Milliseconds { get; set; }
        public List<IoDevice> DiskList = new(), NetworkList = new();
        public IReadOnlyList<IoDevice> Disks() => DiskList;
        public IReadOnlyList<IoDevice> Networks() => NetworkList;
    }

    [Fact]
    public void RatesArePerSecondAndHistoryIsOldestFirst()
    {
        var f = new Fake();
        f.DiskList.Add(new("nvme0", 0, 0)); f.NetworkList.Add(new("eth0", 0, 0, ActivityMonitor.LinkUp | ActivityMonitor.Primary, 0xC0A80002, 0x525400123456));
        var m = new ActivityMonitor(f);
        m.Tick();
        f.Milliseconds = 1000; f.DiskList[0] = new("nvme0", 4096, 1024); f.NetworkList[0] = f.NetworkList[0] with { In = 2000, Out = 500 };
        m.Tick();
        f.Milliseconds = 2000; f.DiskList[0] = new("nvme0", 4096 + 8192, 1024);
        var s = m.Snapshot(new[] { new ActivityWindow(42, "Polaris", true, false) });
        Span<byte> b = s;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(b[12..]));
        Assert.Equal(8192u, BinaryPrimitives.ReadUInt32LittleEndian(b[(ActivityMonitor.DisksOffset + 32)..]));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(b[(ActivityMonitor.HistoryOffset)..]));
        Assert.Equal(2000u, BinaryPrimitives.ReadUInt32LittleEndian(b[(ActivityMonitor.HistoryOffset + 8)..]));
        Assert.Equal(8192u, BinaryPrimitives.ReadUInt32LittleEndian(b[(ActivityMonitor.HistoryOffset + 16)..]));
        int n = ActivityMonitor.NetworksOffset;
        Assert.Equal((byte)'e', b[n]); Assert.Equal(0xC0A80002u, BinaryPrimitives.ReadUInt32BigEndian(b[(n + 44)..]));
        Assert.Equal(0x52, b[n + 48]); Assert.Equal(0x56, b[n + 53]);
        int w = ActivityMonitor.WindowsOffset;
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(b[w..])); Assert.Equal(ActivityMonitor.Focused, BinaryPrimitives.ReadUInt32LittleEndian(b[(w + 4)..]));
        Assert.Equal((byte)'P', b[w + 8]);
    }

    [Fact]
    public void AReplacedOrResetCounterReadsZeroNotHuge()
    {
        var f = new Fake(); f.DiskList.Add(new("usb0", 10_000, 0));
        var m = new ActivityMonitor(f); m.Tick();
        f.Milliseconds = 1000; f.DiskList[0] = new("usb0", 10, 0); m.Tick();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(m.Snapshot(Array.Empty<ActivityWindow>()).AsSpan(ActivityMonitor.DisksOffset + 32)));
    }

    [Fact]
    public void LayoutFitsThePage()
    {
        Assert.Equal(ActivityMonitor.NetworksOffset, ActivityMonitor.DisksOffset + ActivityMonitor.MaximumDisks * ActivityMonitor.DiskBytes);
        Assert.Equal(ActivityMonitor.HistoryOffset, ActivityMonitor.NetworksOffset + ActivityMonitor.MaximumNetworks * ActivityMonitor.NetworkBytes);
        Assert.Equal(ActivityMonitor.WindowsOffset, ActivityMonitor.HistoryOffset + ActivityMonitor.History * ActivityMonitor.SampleBytes);
        Assert.Equal(ActivityMonitor.SnapshotBytes, ActivityMonitor.WindowsOffset + ActivityMonitor.MaximumWindows * ActivityMonitor.WindowBytes);
    }
}
