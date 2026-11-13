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
    public static readonly ActivityMonitor Activity = new(new ActivitySources());
    /// <summary>The desktop's windows, republished by its tick (an immutable array: calls read it
    /// from process threads while the desktop owns the window list).</summary>
    public static volatile ActivityWindow[] Windows = System.Array.Empty<ActivityWindow>();

    private sealed class ActivitySources : IActivitySources
    {
        public long Milliseconds => Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
        public System.Collections.Generic.IReadOnlyList<IoDevice> Disks()
        {
            var list = new System.Collections.Generic.List<IoDevice>();
            foreach (var disk in Cosmos.Kernel.System.Storage.StorageManager.Devices)
            {
                var (read, written) = IoCounters.Of(disk.Name);
                list.Add(new IoDevice(disk.Name, read, written));
            }
            return list;
        }
        public System.Collections.Generic.IReadOnlyList<IoDevice> Networks()
        {
            // The stack counts all adapters together; Zenith sends and receives on the primary one.
            var list = new System.Collections.Generic.List<IoDevice>();
            var networking = Cosmos.Kernel.System.Network.NetworkManager.Primary;
            for (int i = 0; i < Cosmos.Kernel.System.Network.NetworkManager.DeviceCount; i++)
            {
                var a = Cosmos.Kernel.System.Network.NetworkManager.GetAdapter(i);
                if (!a.IsValid) { continue; }
                bool primary = a == networking;
                uint flags = (a.LinkUp ? ActivityMonitor.LinkUp : 0) | (a.Ready ? ActivityMonitor.Ready : 0) | (primary ? ActivityMonitor.Primary : 0);
                uint address = 0;
                var ip = a.IPConfig?.Address;
                if (ip is not null && ip.IsIpv4) { var bytes = ip.ToBytes(); if (bytes.Length == 4) { address = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]); } }
                ulong mac = 0;
                string? text = a.MacAddress?.ToString();
                if (text is not null)
                {
                    foreach (char c in text)
                    {
                        int v = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                        if (v >= 0) { mac = (mac << 4) | (uint)v; }
                    }
                }
                list.Add(new IoDevice("eth" + i, primary ? (ulong)Zenith.Core.Network.Transport.EthernetIngress.Bytes : 0,
                    primary ? (ulong)Zenith.Core.Network.Transport.EthernetEgress.Bytes : 0, flags, address, mac & 0xFFFFFFFFFFFFUL));
            }
            return list;
        }
    }

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
        private ulong _installed;
        public unsafe ulong MemoryInstalled
        {
            get
            {
                if (_installed != 0) { return _installed; }
                var response = Cosmos.Kernel.Boot.Limine.Limine.MemoryMap.Response;
                if (response == null) { return 0; }
                ulong sum = 0;
                for (ulong i = 0; i < response->EntryCount; i++)
                {
                    var entry = response->Entries[i];
                    if (entry->Type is Cosmos.Kernel.Boot.Limine.LimineMemmapType.Usable or Cosmos.Kernel.Boot.Limine.LimineMemmapType.AcpiReclaimable
                        or Cosmos.Kernel.Boot.Limine.LimineMemmapType.BootloaderReclaimable or Cosmos.Kernel.Boot.Limine.LimineMemmapType.KernelAndModules)
                    { sum += entry->Length; }
                }
                return _installed = sum;
            }
        }
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
