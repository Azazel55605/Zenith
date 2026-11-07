using System;
using System.Buffers.Binary;
using System.Text;

namespace Zenith.Core.Monitoring;

/// <summary>What the monitor reads. The kernel binds it to CPU idle counters, memory and frame stats.</summary>
internal interface IMonitorSources
{
    int Cpus { get; }
    int DetectedCpus { get; }
    bool OutOfService(int cpu);
    ulong IdleTicks(int cpu);
    ulong Ticks { get; }
    long Milliseconds { get; }
    ulong MemoryTotal { get; }
    ulong MemoryFree { get; }
    /// <summary>Presented frames, scene frames and their total time since boot, and the worst frame since the last call.</summary>
    (long Presented, long Scenes, long Micros, long Worst) TakeFrames();
    string Processor { get; }
}

/// <summary>
/// Observatory's system sampler (call 642). One sample per second at most, kept for two minutes,
/// so a window opened now already has a graph. Reading never blocks on a service.
/// </summary>
internal sealed class SystemMonitor
{
    public const int SnapshotBytes = 4096, History = 120, SampleBytes = 16, HistoryOffset = 256, MaximumCpus = 8, IntervalMs = 1000;
    public const byte Offline = 255;
    private readonly IMonitorSources _sources;
    private readonly object _gate = new();
    private readonly byte[] _ring = new byte[History * SampleBytes];
    private readonly ulong[] _idle = new ulong[MaximumCpus];
    private ulong _ticks; private long _sampled = long.MinValue, _presented, _scenes, _micros;
    private int _count, _next;
    private byte[] _latest = new byte[SampleBytes];
    private uint _fps, _average, _worst;
    public SystemMonitor(IMonitorSources sources) { _sources = sources; }

    /// <summary>Takes a sample when a second has passed. Cheap otherwise; the desktop calls it every frame.</summary>
    public void Tick()
    {
        long now = _sources.Milliseconds;
        lock (_gate)
        {
            if (_sampled != long.MinValue && now - _sampled < IntervalMs) { return; }
            ulong ticks = _sources.Ticks; int cpus = Math.Min(MaximumCpus, _sources.Cpus);
            var sample = new byte[SampleBytes]; int busySum = 0, counted = 0;
            for (int i = 0; i < MaximumCpus; i++)
            {
                if (i >= cpus || _sources.OutOfService(i)) { sample[i] = Offline; continue; }
                ulong idle = _sources.IdleTicks(i);
                if (_sampled != long.MinValue && ticks > _ticks)
                {
                    ulong span = ticks - _ticks, halted = idle >= _idle[i] ? Math.Min(idle - _idle[i], span) : 0;
                    int busy = (int)(100 - halted * 100 / span);
                    sample[i] = (byte)busy; busySum += busy; counted++;
                }
                _idle[i] = idle;
            }
            sample[8] = (byte)(counted == 0 ? 0 : (busySum + counted / 2) / counted);
            ulong total = _sources.MemoryTotal, free = Math.Min(_sources.MemoryFree, total);
            sample[9] = (byte)(total == 0 ? 0 : (total - free) * 100 / total);
            var frames = _sources.TakeFrames();
            if (_sampled != long.MinValue && now > _sampled)
            {
                long scenes = frames.Scenes - _scenes;
                _fps = (uint)Math.Max(0, (frames.Presented - _presented) * 1000 / (now - _sampled));
                _average = scenes > 0 ? (uint)((frames.Micros - _micros) / scenes) : 0;
                _worst = (uint)Math.Max(0, frames.Worst);
                sample.CopyTo(_ring, _next * SampleBytes); _next = (_next + 1) % History; _count = Math.Min(History, _count + 1);
                _latest = sample;
            }
            _presented = frames.Presented; _scenes = frames.Scenes; _micros = frames.Micros;
            _ticks = ticks; _sampled = now;
        }
    }

    public byte[] Snapshot(int programs)
    {
        Tick();
        var data = new byte[SnapshotBytes]; Span<byte> b = data;
        lock (_gate)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)Math.Min(MaximumCpus, _sources.Cpus));
            BinaryPrimitives.WriteUInt32LittleEndian(b[8..], (uint)Math.Max(0, _sources.DetectedCpus));
            BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)_count);
            BinaryPrimitives.WriteUInt64LittleEndian(b[16..], (ulong)Math.Max(0, _sources.Milliseconds));
            BinaryPrimitives.WriteUInt64LittleEndian(b[24..], _sources.MemoryTotal);
            BinaryPrimitives.WriteUInt64LittleEndian(b[32..], Math.Min(_sources.MemoryFree, _sources.MemoryTotal));
            BinaryPrimitives.WriteUInt32LittleEndian(b[40..], (uint)Math.Max(0, programs));
            BinaryPrimitives.WriteUInt32LittleEndian(b[44..], _fps);
            BinaryPrimitives.WriteUInt32LittleEndian(b[48..], _average);
            BinaryPrimitives.WriteUInt32LittleEndian(b[52..], _worst);
            BinaryPrimitives.WriteUInt32LittleEndian(b[56..], IntervalMs);
            // How old the newest sample is, so a client can glide its graph in step with sampling.
            BinaryPrimitives.WriteUInt32LittleEndian(b[60..], _count == 0 ? 0 : (uint)Math.Clamp(_sources.Milliseconds - _sampled, 0, 60000));
            _latest.CopyTo(b[64..]);
            byte[] name = Encoding.ASCII.GetBytes(_sources.Processor);
            name.AsSpan(0, Math.Min(63, name.Length)).CopyTo(b[128..]);
            // Oldest first, so a client can draw left to right without reordering.
            for (int i = 0; i < _count; i++)
            {
                int slot = (_next - _count + i + History) % History;
                _ring.AsSpan(slot * SampleBytes, SampleBytes).CopyTo(b[(HistoryOffset + i * SampleBytes)..]);
            }
        }
        return data;
    }
}
