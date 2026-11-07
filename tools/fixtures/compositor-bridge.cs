using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Zenith.Core.Multiprocessing;

namespace Cosmos.Kernel.System.Diagnostics { internal static class MemoryInfo { internal static ulong FreePages = 1000000; } }

namespace Zenith.Core.Shell
{
    internal sealed class CommandContext { internal void WriteLine(string text) { } }
}
namespace Zenith.Core.Multiprocessing
{
    internal static class ManagedCpuJobs { internal static void PumpRequests() { } }
    internal static partial class CpuWorkers
    {
        private static readonly object s_gate = new();
        private static int s_count = 4;
        private static bool s_stopping;
        [DllImport("*", EntryPoint="zenith_cpu_job")] private static extern ulong Job(ulong token, ulong field);
        [DllImport("*", EntryPoint="zenith_cpu_control")] private static extern ulong Control(ulong token, ulong action);
        private static ulong WaitInterrupt(ulong token) { Thread.Yield(); return 1; }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        internal static object Gate => s_gate;
        internal static void StopAdmission(bool value) => s_stopping = value;
        internal static void CpuCount(int value) => s_count = value;
    }
}

internal static class Program
{
    [DllImport("*", EntryPoint="zenith_cpu_host_init")] private static extern void Init(ulong count);
    [DllImport("*", EntryPoint="zenith_cpu_host_step")] private static extern ulong Step(ulong cpu);
    [DllImport("*", EntryPoint="zenith_cpu_host_state")] private static extern void State(ulong cpu, ulong state, ulong halted);
    private static volatile bool s_stop;
    private static void Check(bool ok, string error) { if (!ok) throw new Exception(error); }
    internal static void Main(string[] args)
    {
        IntPtr library = NativeLibrary.Load(args[0]);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, assembly, path) => name == "*" ? library : IntPtr.Zero);
        Init(4);
        const int w = 521, h = 139, offset = 7, area = w * h;
        int[] source = new int[area + 14], scratch = new int[source.Length], expected = new int[source.Length], actual = new int[source.Length];
        var random = new Random(17); for (int i = 0; i < source.Length; i++) source[i] = random.Next(int.MinValue, int.MaxValue);
        int[] untouched = (int[])source.Clone();
        BoxBlur.Pass(source, scratch, offset, w, h, true); BoxBlur.Pass(scratch, expected, offset, w, h, false);
        CpuWorkers.ParallelBlur = false;
        Check(!CpuWorkers.TryBlur(source, actual, offset, w, h), "off admission");
        CpuWorkers.ParallelBlur = true; CpuWorkers.CpuCount(1);
        Check(!CpuWorkers.TryBlur(source, actual, offset, w, h), "one CPU admission");
        CpuWorkers.CpuCount(4); CpuWorkers.StopAdmission(true);
        Check(!CpuWorkers.TryBlur(source, actual, offset, w, h), "shutdown admission");
        CpuWorkers.StopAdmission(false);
        Check(!CpuWorkers.TryBlur(source, source, offset, w, h), "in-place source mutation");
        Check(!CpuWorkers.TryBlur(source, actual, -1, w, h), "negative offset");
        Check(!CpuWorkers.TryBlur(source, actual, offset, 4097, h), "unbounded width");
        Check(!CpuWorkers.TryBlur(source, actual, offset, w, int.MaxValue), "overflow height");
        Check(!CpuWorkers.TryBlur(source, actual, source.Length, w, h), "source escape");
        Check(!CpuWorkers.TryBlur(source, new int[1], offset, w, h), "target escape");
        for (ulong cpu = 1; cpu < 4; cpu++) State(cpu, 4, 1);
        Check(!CpuWorkers.TryBlur(source, actual, offset, w, h), "all owners unavailable");
        Init(4);
        var threads = new List<Thread>();
        for (ulong cpu = 1; cpu < 4; cpu++)
        {
            ulong selected = cpu;
            var thread = new Thread(() => { while (!s_stop) { if (Step(selected) == 0) Thread.Yield(); } });
            threads.Add(thread); thread.Start();
        }
        try
        {
            for (int iteration = 0; iteration < 40; iteration++)
            {
                Array.Fill(actual, 0x12345678);
                Check(CpuWorkers.TryBlur(source, actual, offset, w, h), "healthy bridge refused: " + CpuWorkers.BlurStatus);
                for (int i = 0; i < area; i++) Check(actual[offset+i] == expected[offset+i], "whole-region pixels/seams");
                for (int i = 0; i < offset; i++) Check(actual[i] == 0x12345678 && actual[offset+area+i] == 0x12345678, "guard overwrite");
                Check(source.AsSpan().SequenceEqual(untouched), "source changed");
            }
            bool? busy = null;
            lock (CpuWorkers.Gate)
            {
                var other = new Thread(() => busy = CpuWorkers.TryBlur(source, actual, offset, w, h));
                other.Start(); Check(other.Join(2000), "busy producer blocked UI");
                Check(busy == false, "concurrent producer admitted");
            }
            const int cw = 1021, ch = 259, fs = cw + 11, bs = cw + 29, fo = 17, bo = 23;
            int[] fg = new int[fo + fs * ch + 32], bg = new int[bo + bs * ch + 32];
            Array.Fill(bg, 0x12345678);
            for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
            {
                int a = (x * 17 + y * 31) % 256;
                fg[fo + y * fs + x] = (a << 24) | ((x * 7 % (a + 1)) << 16) | ((y * 13 % (a + 1)) << 8) | ((x + y) % (a + 1));
            }
            int[] originalFg = (int[])fg.Clone(), composed = (int[])bg.Clone();
            for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
            {
                int f = fg[fo + y * fs + x], at = bo + y * bs + x, a = (int)((uint)f >> 24), b = bg[at];
                if (a == 255) { composed[at] = f; continue; }
                if (a == 0) { continue; }
                uint result = 0xff000000;
                for (int shift = 0; shift < 24; shift += 8)
                    result |= (uint)(((f >> shift) & 255) + (((b >> shift) & 255) * (255 - a) + 127) / 255) << shift;
                composed[at] = (int)result;
            }
            for (int frame = 0; frame < 8; frame++)
            {
                int[] output = (int[])bg.Clone();
                Check(CpuWorkers.TryCompose(fg, fo, fs, output, bo, bs, cw, ch), "composition refused");
                Check(output.AsSpan().SequenceEqual(composed), "composition pixel/guard mismatch");
                Check(fg.AsSpan().SequenceEqual(originalFg), "composition changed foreground");
            }
            CpuWorkers.ParallelBlur = false;
            Check(!CpuWorkers.TryCompose(fg, fo, fs, bg, bo, bs, cw, ch), "composition ignores off");
            CpuWorkers.ParallelBlur = true;
            Check(!CpuWorkers.TryCompose(fg, int.MaxValue, fs, bg, bo, bs, cw, ch), "composition source escape");
            bool? composeBusy = null;
            lock (CpuWorkers.Gate)
            {
                var other = new Thread(() => composeBusy = CpuWorkers.TryCompose(fg, fo, fs, bg, bo, bs, cw, ch));
                other.Start(); Check(other.Join(2000), "composition busy blocks UI");
                Check(composeBusy == false, "composition concurrent producer admitted");
            }
            Console.WriteLine("PASS R3 production C# composition: eight exact clipped/strided frames, guards, immutable source and bounded fallback");
            Console.WriteLine("PASS C4 production C# bridge / native queues: 40 complete regions, exact pixels, guards, busy/off/shutdown/CPU/bounds fallback");
        }
        finally { s_stop = true; foreach (Thread thread in threads) thread.Join(); }
    }
}
