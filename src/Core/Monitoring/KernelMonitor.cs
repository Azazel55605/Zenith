using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cosmos.Kernel.System.Diagnostics;
using Zenith.Core.Multiprocessing;

namespace Zenith.Core.Monitoring;

/// <summary>The kernel's monitor, bound to CPU idle counters, memory and the compositor's frame stats.</summary>
internal static class KernelMonitor
{
    /// <summary>The desktop sets this to its frame counters; without a desktop there are no frames.</summary>
    public static Func<(long Presented, long Scenes, long Micros, long Worst)>? Frames;
    public static readonly SystemMonitor Instance = new(new Sources());

    [DllImport("*", EntryPoint = "zenith_cpu_brand"), SuppressGCTransition]
    private static extern unsafe ulong Brand(uint* output);

    private sealed class Sources : IMonitorSources
    {
        private string? _processor;
        public int Cpus => CpuWorkers.Online;
        public int DetectedCpus => CpuWorkers.DetectedCpus;
        public bool OutOfService(int cpu) => CpuWorkers.OutOfService(cpu);
        public ulong IdleTicks(int cpu) => CpuWorkers.IdleTicks(cpu);
        public ulong Ticks => CpuWorkers.Now;
        public long Milliseconds => Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        public ulong MemoryTotal => MemoryInfo.TotalPages * 4096UL;
        public ulong MemoryFree => MemoryInfo.FreePages * 4096UL;
        public (long Presented, long Scenes, long Micros, long Worst) TakeFrames() => Frames?.Invoke() ?? (0, 0, 0, 0);
        public unsafe string Processor
        {
            get
            {
                if (_processor is not null) { return _processor; }
                uint* words = stackalloc uint[12];
                ulong length = Brand(words);
                var text = new StringBuilder();
                for (int i = 0; i < (int)length; i++)
                {
                    char c = (char)((words[i / 4] >> (i % 4 * 8)) & 0xff);
                    if (c == 0) { break; }
                    if (c is >= ' ' and < (char)127 && !(c == ' ' && (text.Length == 0 || text[^1] == ' '))) { text.Append(c); }
                }
                return _processor = text.ToString().Trim();
            }
        }
    }
}
