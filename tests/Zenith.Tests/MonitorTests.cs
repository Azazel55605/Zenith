using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Zenith.Core.Monitoring;
using Zenith.Core.Processes;

namespace Zenith.Tests;

public class MonitorTests
{
    private sealed class Fake : IMonitorSources
    {
        public int Cpus { get; set; } = 2;
        public int DetectedCpus { get; set; } = 4;
        public HashSet<int> Broken = new();
        public ulong[] Idle = new ulong[8];
        public ulong Ticks { get; set; }
        public long Milliseconds { get; set; }
        public ulong MemoryTotal { get; set; } = 4UL << 30;
        public ulong MemoryFree { get; set; } = 3UL << 30;
        public ulong MemoryInstalled { get; set; } = 6UL << 30;
        public (long, long, long, long) Frame = (0, 0, 0, 0);
        public bool OutOfService(int cpu) => Broken.Contains(cpu);
        public ulong IdleTicks(int cpu) => Idle[cpu];
        public (long Presented, long Scenes, long Micros, long Worst) TakeFrames() => Frame;
        public string Processor => "Test CPU @ 1 GHz";
    }

    private static byte[] Sample(SystemMonitor m, int index)
    {
        byte[] s = m.Snapshot(3);
        return s.AsSpan(SystemMonitor.HistoryOffset + index * SystemMonitor.SampleBytes, SystemMonitor.SampleBytes).ToArray();
    }

    [Fact]
    public void BusyIsTheShareOfTicksNotHaltedPerCpu()
    {
        var f = new Fake(); var m = new SystemMonitor(f);
        m.Tick(); // baseline only
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(m.Snapshot(0).AsSpan(12)));
        f.Milliseconds = 1000; f.Ticks = 1000; f.Idle[0] = 250; f.Idle[1] = 1000; f.Frame = (60, 50, 50 * 4000, 19000);
        m.Tick();
        byte[] s = Sample(m, 0);
        Assert.Equal(75, s[0]); Assert.Equal(0, s[1]); Assert.Equal(SystemMonitor.Offline, s[2]);
        Assert.Equal(38, s[8]); Assert.Equal(25, s[9]);
        byte[] snapshot = m.Snapshot(3);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(12)));
        Assert.Equal(1000UL, BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(16)));
        Assert.Equal(6UL << 30, BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(112)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(40)));
        Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(44)));
        Assert.Equal(4000u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(48)));
        Assert.Equal(19000u, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(52)));
        f.Milliseconds = 1400; Assert.Equal(400u, BinaryPrimitives.ReadUInt32LittleEndian(m.Snapshot(3).AsSpan(60)));
        Assert.StartsWith("Test CPU @ 1 GHz\0", Encoding.ASCII.GetString(snapshot, 128, 64));
    }

    [Fact]
    public void SamplesAtMostOncePerSecondAndKeepsTwoMinutesOldestFirst()
    {
        var f = new Fake { Cpus = 1 }; var m = new SystemMonitor(f);
        m.Tick();
        for (int i = 1; i <= 130; i++)
        {
            f.Milliseconds = i * 1000; f.Ticks = (ulong)i * 100; f.Idle[0] = (ulong)(i * 100 - i % 100);
            m.Tick(); f.Milliseconds += 500; m.Tick(); // the second call is too soon
        }
        byte[] snapshot = m.Snapshot(0);
        Assert.Equal((uint)SystemMonitor.History, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(12)));
        // Sample i is i%100 - (i-1)%100 busy ticks out of 100: oldest kept is i=11, newest i=130.
        Assert.Equal(1, Sample(m, 0)[0]);
        Assert.Equal(1, Sample(m, SystemMonitor.History - 1)[0]);
        Assert.Equal(0, Sample(m, 89)[0] - 0); // i=100 wraps: 0 - 99 clamps to 0 busy
    }

    [Fact]
    public void FaultedWorkerIsOfflineAndIdleNeverExceedsTheSpan()
    {
        var f = new Fake { Cpus = 3 }; f.Broken.Add(2); var m = new SystemMonitor(f);
        m.Tick(); f.Milliseconds = 1000; f.Ticks = 100; f.Idle[0] = 500; f.Idle[1] = 0; m.Tick();
        byte[] s = Sample(m, 0);
        Assert.Equal(0, s[0]); Assert.Equal(100, s[1]); Assert.Equal(SystemMonitor.Offline, s[2]); Assert.Equal(50, s[8]);
    }

    [Fact]
    public void ProcessListPutsLiveProgramsFirstAndFlagsStopsAndExits()
    {
        var table = new ProcessTable(8, 4);
        int a = table.Start("polaris", 1000, () => { }), b = table.Start("cat", 1000, () => { }), c = table.Start("pkg", 0, () => { });
        table.SetState(a, ProcessState.Waiting); table.RequestStop(b, 1000); table.Complete(c, 3);
        var names = new List<uint>();
        byte[] all = ProcessMonitor.Snapshot(table.List(0), uid => { names.Add(uid); return uid == 0 ? "root" : "azazel"; });
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(4)));
        Span<byte> e0 = all.AsSpan(ProcessMonitor.HeaderBytes, ProcessMonitor.EntryBytes);
        Span<byte> e1 = all.AsSpan(ProcessMonitor.HeaderBytes + ProcessMonitor.EntryBytes, ProcessMonitor.EntryBytes);
        Span<byte> e2 = all.AsSpan(ProcessMonitor.HeaderBytes + 2 * ProcessMonitor.EntryBytes, ProcessMonitor.EntryBytes);
        Assert.Equal(a, BinaryPrimitives.ReadInt32LittleEndian(e0)); Assert.Equal((byte)ProcessState.Waiting, e0[8]);
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(e0[12..]));
        Assert.StartsWith("polaris\0", Encoding.ASCII.GetString(e0.Slice(16, 32))); Assert.StartsWith("azazel\0", Encoding.ASCII.GetString(e0.Slice(48, 16)));
        Assert.Equal(ProcessMonitor.StopRequested, e1[9]);
        Assert.Equal(c, BinaryPrimitives.ReadInt32LittleEndian(e2)); Assert.Equal(ProcessMonitor.Exited, e2[9]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(e2[12..])); Assert.StartsWith("root\0", Encoding.ASCII.GetString(e2.Slice(48, 16)));
        Assert.Equal(2, names.Count); // one lookup per account
        byte[] own = ProcessMonitor.Snapshot(table.List(1000), _ => "azazel");
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(own.AsSpan(4)));
    }

    [Fact]
    public void LongNamesAreClippedAndTerminated()
    {
        var table = new ProcessTable(); table.Start(new string('n', 64), 5, () => { });
        byte[] data = ProcessMonitor.Snapshot(table.List(0), _ => new string('u', 40));
        Assert.Equal(0, data[ProcessMonitor.HeaderBytes + 16 + 31]); Assert.Equal((byte)'n', data[ProcessMonitor.HeaderBytes + 16 + 30]);
        Assert.Equal(0, data[ProcessMonitor.HeaderBytes + 48 + 15]);
    }
}
