using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Boot.Limine;
using Zenith.Core.Security;
using Zenith.Core.Shell;

namespace Zenith.Core.Multiprocessing;

/// <summary>Opt-in native integer workers; no managed execution or GC roots on APs.</summary>
internal static class CpuWorkers
{
    private static readonly object s_gate = new();
    private static int s_count;
    private static bool s_failed;
    [DllImport("*", EntryPoint = "zenith_cpu_detected"), SuppressGCTransition]
    private static extern ulong Detected();
    [DllImport("*", EntryPoint = "zenith_cpu_initialize"), SuppressGCTransition]
    private static extern ulong InitializeNative();
    [DllImport("*", EntryPoint = "zenith_cpu_field"), SuppressGCTransition]
    private static extern ulong Field(ulong cpu, ulong field);
    [DllImport("*", EntryPoint = "zenith_cpu_submit"), SuppressGCTransition]
    private static extern void Submit(ulong iterations);
    [DllImport("*", EntryPoint = "zenith_cpu_execute"), SuppressGCTransition]
    private static extern void Execute();

    [DllImport("*", EntryPoint = "zenith_initial_idt_flags"), SuppressGCTransition]
    private static extern ulong InitialIdtFlags();
    [DllImport("*", EntryPoint = "zenith_idt_irq_test"), SuppressGCTransition]
    private static extern int IdtIrqTest();

    public static unsafe void Initialize()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return; }
        string args = Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "";
        if (Array.IndexOf(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), "smp=native") < 0) { return; }
        s_count = (int)InitializeNative();
        if (s_count > 0 && !Wait(3)) { s_failed = true; }
        Log.Write("cpu", "native workers=" + s_count + " ready=" + (!s_failed) + "; managed scheduler remains BSP-only");
    }

    private static bool Wait(ulong state)
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            bool ready = true;
            for (int i = 1; i < s_count; i++)
            {
                ulong current = Field((ulong)i, 0);
                if (current == 4) { return false; }
                ready &= current == state;
            }
            if (ready) { return true; }
            Thread.Sleep(1);
        }
        return false;
    }

    public static int Command(CommandContext c)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return c.Fail("native CPU workers require x86_64"); }
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "status"))
        {
            c.WriteLine("detected=" + Detected() + " native-workers=" + s_count + " failed=" + s_failed + " managed-cpus=1");
            c.WriteLine("initial-idt-interrupts-enabled=" + ((InitialIdtFlags() & 0x200) != 0));
            c.WriteLine("Experimental workers require boot argument smp=native; idle APs currently poll.");
            return 0;
        }
        if (c.Args.Length == 1 && c.Args[0] == "irq-test")
        {
            GuardedFilesystem.RequireRoot();
            int result = IdtIrqTest();
            c.WriteLine("IDT interrupt-state preservation failures=" + result);
            return result == 0 ? 0 : 1;
        }
        if (c.Args.Length > 2 || c.Args[0] != "test") { return c.Fail("cpu status | irq-test | test [iterations]"); }
        GuardedFilesystem.RequireRoot();
        ulong iterations = 2000000;
        if (c.Args.Length == 2 && !ulong.TryParse(c.Args[1], out iterations)) { return c.Fail("invalid iteration count"); }
        if (iterations < 1 || iterations > 10000000) { return c.Fail("iterations must be 1..10000000"); }
        lock (s_gate)
        {
            if (s_count < 2 || s_failed) { return c.Fail("at least two ready native workers required; boot with smp=native"); }
            if (!Wait(3)) { s_failed = true; return c.Fail("native workers unavailable"); }
            Submit(iterations);
            if (!Wait(2)) { s_failed = true; return c.Fail("native worker barrier failed; workers disabled"); }
            Execute();
            if (!Wait(3)) { s_failed = true; return c.Fail("native worker completion failed; workers disabled"); }
            ulong expected = 0xcbf29ce484222325;
            for (ulong i = iterations; i > 0; i--) { expected = unchecked((expected ^ i) * 0x100000001b3); }
            ulong latestStart = 0, earliestEnd = ulong.MaxValue;
            for (int i = 0; i < s_count; i++)
            {
                ulong index = (ulong)i, start = Field(index, 3), end = Field(index, 4);
                ulong id = Field(index, 2), actual = Field(index, 6), result = Field(index, 5);
                c.WriteLine("worker=" + i + " apic=" + id + " actual=" + actual + " start=" + start + " end=" + end + " result=" + result.ToString("x"));
                if (id != actual || result != expected || start >= end) { s_failed = true; return c.Fail("native worker result/identity mismatch"); }
                latestStart = Math.Max(latestStart, start); earliestEnd = Math.Min(earliestEnd, end);
            }
            c.WriteLine("native CPU results verified; overlap=" + (latestStart < earliestEnd) + "; managed-cpus=1");
            return 0;
        }
    }
}
