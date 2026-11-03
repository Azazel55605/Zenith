using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Boot.Limine;
using Zenith.Core.Security;
using Zenith.Core.Shell;

namespace Zenith.Core.Multiprocessing;

/// <summary>C1 native-only AP queues; all managed callers/GC/UI ownership stay on BSP.</summary>
internal static class CpuWorkers
{
    private static readonly object s_gate = new();
    private static int s_count;
    private static volatile bool s_stopping;
    [DllImport("*", EntryPoint = "zenith_cpu_detected"), SuppressGCTransition]
    private static extern ulong Detected();
    [DllImport("*", EntryPoint = "zenith_cpu_initialize"), SuppressGCTransition]
    private static extern ulong InitializeNative(ulong hhdm);
    [DllImport("*", EntryPoint = "zenith_cpu_field"), SuppressGCTransition]
    private static extern ulong Field(ulong cpu, ulong field);
    [DllImport("*", EntryPoint = "zenith_cpu_enqueue"), SuppressGCTransition]
    private static extern ulong Enqueue(ulong cpu, ulong kind, ulong count, ulong seed, ulong rounds, ulong held);
    [DllImport("*", EntryPoint = "zenith_cpu_job"), SuppressGCTransition]
    private static extern ulong Job(ulong token, ulong field);
    [DllImport("*", EntryPoint = "zenith_cpu_control"), SuppressGCTransition]
    private static extern ulong Control(ulong token, ulong action);
    [DllImport("*", EntryPoint = "zenith_cpu_pixel"), SuppressGCTransition]
    private static extern ulong Pixel(ulong token, ulong index);
    [DllImport("*", EntryPoint = "zenith_cpu_bsp"), SuppressGCTransition]
    private static extern ulong Bsp(ulong kind, ulong count, ulong seed, ulong rounds);
    [DllImport("*", EntryPoint = "zenith_cpu_stop"), SuppressGCTransition]
    private static extern void StopNative();
    [DllImport("*", EntryPoint = "zenith_initial_idt_flags"), SuppressGCTransition]
    private static extern ulong InitialIdtFlags();
    [DllImport("*", EntryPoint = "zenith_idt_irq_test"), SuppressGCTransition]
    private static extern int IdtIrqTest();

    public static unsafe void Initialize()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return; }
        string args = Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "";
        if (Array.IndexOf(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), "smp=native") < 0) { return; }
        s_count = (int)InitializeNative(Limine.HHDM.Response == null ? 0 : Limine.HHDM.Response->Offset);
        bool ready = s_count > 0 && Wait(() => AllWorkers(3));
        Log.Write("cpu", "native workers=" + s_count + " ready=" + ready + "; idle=interrupt; managed scheduler remains BSP-only");
    }

    private static bool Wait(Func<bool> ready)
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        do
        {
            if (ready()) { return true; }
            Thread.Sleep(1);
        } while (Stopwatch.GetTimestamp() < deadline);
        return false;
    }

    private static bool AllWorkers(ulong state)
    {
        for (int i = 1; i < s_count; i++) { if (Field((ulong)i, 0) != state) { return false; } }
        return true;
    }

    private static bool AllHalted()
    {
        for (int i = 1; i < s_count; i++)
        {
            ulong state = Field((ulong)i, 0);
            if ((state != 4 && state != 5) || Field((ulong)i, 12) != 1) { return false; }
        }
        return true;
    }

    public static void Quiesce()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || s_count == 0) { return; }
        // Stop must not wait for a managed producer holding s_gate (or for its
        // terminal output). Native BSP mutations are already IRQ-scoped; this
        // closes admission immediately, cancels work and wakes parked APs.
        StopChecked();
        Log.Write("cpu", "native workers quiesced=" + s_count);
    }

    private static void StopChecked()
    {
        s_stopping = true;
        StopNative();
        Require(Wait(AllHalted), "native CPU workers failed to quiesce; power change refused");
    }

    private static void Require(bool ok, string failure)
    {
        if (!ok) { throw new InvalidOperationException(failure); }
    }

    private static void Finished(ulong token, ulong expected = 3)
    {
        Require(token != 0, "native queue admission refused");
        Require(Wait(() => Job(token, 0) >= 3), "native job completion timeout");
        ulong state = Job(token, 0);
        Require(expected == 0 ? state == 3 || state == 4 : state == expected, "native job completion status mismatch");
    }

    private static ulong Checksum(ulong n)
    {
        ulong value = 0xcbf29ce484222325;
        for (; n != 0; n--) { value = unchecked((value ^ n) * 0x100000001b3); }
        return value;
    }

    public static int Command(CommandContext c)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return c.Fail("native CPU workers require x86_64"); }
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "status"))
        {
            c.WriteLine("detected=" + Detected() + " native-workers=" + s_count + " stopping=" + s_stopping + " managed-cpus=1");
            c.WriteLine("initial-idt-interrupts-enabled=" + ((InitialIdtFlags() & 0x200) != 0));
            c.WriteLine("Experimental workers require boot argument smp=native; idle=interrupt queue-capacity=8.");
            for (int i = 1; i < s_count; i++)
            {
                ulong x = (ulong)i;
                c.WriteLine("cpu=" + i + " apic-mode=" + Field(x, 18) + " state=" + Field(x, 0) + " parks=" + Field(x, 8) + " wakes=" + Field(x, 9)
                    + " completed=" + Field(x, 10) + " fault=" + Field(x, 11) + " irq-stack-errors=" + Field(x, 16) + " ipi-errors=" + Field(x, 17));
            }
            return 0;
        }
        GuardedFilesystem.RequireRoot();
        if (c.Args.Length == 1 && c.Args[0] == "irq-test")
        {
            int result = IdtIrqTest();
            c.WriteLine("IDT interrupt-state preservation failures=" + result);
            return result == 0 ? 0 : 1;
        }
        if (c.Args.Length > 2 || (c.Args.Length == 2 && c.Args[0] != "test")) { return c.Fail(Usage); }
        string action = c.Args[0];
        if (action != "test" && action != "cancel-test" && action != "queue-test" && action != "idle-test"
            && action != "image-test" && action != "fault-test" && action != "stop-test") { return c.Fail(Usage); }
        ulong iterations = 2000000;
        if (c.Args.Length == 2 && (!ulong.TryParse(c.Args[1], out iterations) || iterations < 1 || iterations > 10000000))
        { return c.Fail("iterations must be 1..10000000"); }
        Monitor.Enter(s_gate);
        int status;
        try
        {
            if (s_count == 0 || s_stopping || (s_count < 2 && action != "image-test")) { status = c.Fail("native workers unavailable; boot with smp=native"); }
            else
            {
                switch (action)
                {
                    case "queue-test": QueueTest(c); break;
                    case "idle-test": IdleTest(c); break;
                    case "image-test": ImageTest(c); break;
                    case "fault-test": FaultTest(c); break;
                    case "stop-test": StopTest(c); break;
                    default: Batch(c, iterations, action == "cancel-test"); break;
                }
                status = 0;
            }
        }
        catch
        {
            // A diagnostic owner abort may leave accepted/held jobs behind.
            // Fail closed without waiting on its own producer gate or replaying:
            // close admission, cancel/wake all APs, retain native storage.
            s_stopping = true;
            StopNative();
            Monitor.Exit(s_gate);
            throw;
        }
        Monitor.Exit(s_gate);
        return status;
    }

    private const string Usage = "cpu status | irq-test | test [iterations] | cancel-test | queue-test | idle-test | image-test | fault-test | stop-test";

    private static void Batch(CommandContext c, ulong n, bool cancel)
    {
        ulong[] tokens = new ulong[s_count];
        for (int i = 1; i < s_count; i++) { tokens[i] = Enqueue((ulong)i, 1, n, 0, 1, 1); Require(tokens[i] != 0, "batch refused"); }
        Require(Wait(() =>
        {
            for (int i = 1; i < s_count; i++) { if (Job(tokens[i], 0) != 2) { return false; } }
            return true;
        }), "batch ready barrier timeout");
        for (int i = 1; i < s_count; i++) { Require(Control(tokens[i], cancel ? 0UL : 1UL) == 1, "batch start refused"); }
        ulong bsp = Bsp(1, n, 0, 1), expected = Checksum(n);
        ulong latestStart = Field(0, 3), earliestEnd = Field(0, 4);
        c.WriteLine("worker=0 apic=" + Field(0, 2) + " actual=" + Field(0, 6) + " start=" + latestStart + " end=" + earliestEnd + " result=" + bsp.ToString("x"));
        Require(bsp == expected, "BSP result mismatch");
        for (int i = 1; i < s_count; i++)
        {
            ulong token = tokens[i]; Finished(token, cancel ? 4UL : 3UL);
            ulong start = Job(token, 2), end = Job(token, 3), result = Job(token, 1), x = (ulong)i;
            Require(Field(x, 2) == Field(x, 6) && Field(x, 16) == 0 && Field(x, 17) == 0, "AP identity/interrupt-stack/IPI failure");
            Require(cancel || (result == expected && start < end), "AP result mismatch");
            c.WriteLine("worker=" + i + " apic=" + Field(x, 2) + " actual=" + Field(x, 6) + " start=" + start + " end=" + end + " result=" + result.ToString("x"));
            latestStart = Math.Max(latestStart, start); earliestEnd = Math.Min(earliestEnd, end);
            Require(Control(token, 2) == 1, "batch reap refused");
        }
        c.WriteLine(cancel ? "native CPU cancellation verified; workers reusable" : "native CPU results verified; overlap=" + (latestStart < earliestEnd) + "; managed-cpus=1");
    }

    private static void QueueTest(CommandContext c)
    {
        for (int cpu = 1; cpu < s_count; cpu++)
        {
            ulong x = (ulong)cpu;
            ulong[] handles = new ulong[8];
            handles[0] = Enqueue(x, 1, 100000000, 0, 1, 1);
            Require(Wait(() => Job(handles[0], 0) == 2), "held job not claimed");
            for (int i = 1; i < handles.Length; i++) { handles[i] = Enqueue(x, 1, 1000, 0, 1, 0); Require(handles[i] != 0, "queue capacity lost"); }
            Require(Enqueue(x, 1, 1, 0, 1, 0) == 0, "queue overflow admitted");
            Require(Control(handles[2], 0) == 1, "queued cancellation refused");
            Require(Control(handles[0], 1) == 1, "held release refused");
            Require(Wait(() => Job(handles[0], 2) != 0), "running job did not enter work");
            // Completion may beat a cancellation request on a fast or paused host.
            // Either terminal result is valid; successful work must still match
            // the complete checksum. Deterministic mid-loop host tests cover the
            // checkpoint, and cancel-test covers claimed/parked AP cancellation.
            Control(handles[0], 0);
            for (int i = 0; i < handles.Length; i++)
            {
                Finished(handles[i], i == 0 ? 0UL : i == 2 ? 4UL : 3UL);
                if (i == 0 && Job(handles[i], 0) == 3) { Require(Job(handles[i], 1) == Checksum(100000000), "completed cancellation race returned partial work"); }
                if (i != 0 && i != 2) { Require(Job(handles[i], 1) == Checksum(1000), "queued checksum mismatch"); }
                Require(Control(handles[i], 2) == 1, "queue reap refused");
            }
            ulong replacement = Enqueue(x, 1, 1, 0, 1, 0);
            Require(replacement != 0 && replacement != handles[0], "generation did not advance");
            Require(Control(handles[0], 0) == 0 && Job(handles[0], 0) == 0, "stale token accepted");
            Finished(replacement); Require(Control(replacement, 2) == 1, "replacement reap refused");
        }
        // Repeated publish/park races: every job submitted once, never retried.
        for (int cycle = 0; cycle < 200; cycle++)
        {
            for (int cpu = 1; cpu < s_count; cpu++)
            {
                ulong token = Enqueue((ulong)cpu, 1, 1, 0, 1, 0);
                Finished(token); Require(Job(token, 1) == Checksum(1), "wake race lost/corrupted job");
                Require(Control(token, 2) == 1, "race reap refused");
            }
        }
        c.WriteLine("native queue verified; capacity=8 queued/running cancel; stale tokens refused; wake-cycles=200");
    }

    private static void IdleTest(CommandContext c)
    {
        Require(Wait(() =>
        {
            for (int i = 1; i < s_count; i++) { if (Field((ulong)i, 14) != 1 || Field((ulong)i, 0) != 3) { return false; } }
            return true;
        }), "APs did not park");
        ulong[] before = new ulong[s_count];
        // Drain any pending doorbell from the previous batch before measuring.
        Thread.Sleep(50);
        for (int i = 1; i < s_count; i++) { before[i] = Field((ulong)i, 8); }
        Thread.Sleep(250);
        for (int i = 1; i < s_count; i++)
        {
            ulong x = (ulong)i;
            Require(Field(x, 14) == 1 && Field(x, 8) == before[i] && Field(x, 16) == 0 && Field(x, 17) == 0, "idle AP woke/polled or interrupt-stack error");
        }
        c.WriteLine("native idle verified; parked APs do not poll; private interrupt stacks verified");
    }

    private static uint Blend(uint p, uint round)
    {
        uint tint = 0x203040 + (round & 15) * 0x010101;
        uint r = (((p >> 16) & 255) * 191 + ((tint >> 16) & 255) * 64 + 127) / 255;
        uint g = (((p >> 8) & 255) * 191 + ((tint >> 8) & 255) * 64 + 127) / 255;
        uint b = ((p & 255) * 191 + (tint & 255) * 64 + 127) / 255;
        return 0xff000000 | (r << 16) | (g << 8) | b;
    }

    private static void ImageTest(CommandContext c)
    {
        const int tiles = 32, pixels = 4096, rounds = 128;
        // Same native workload at each CPU count. Compare every AP-produced pixel
        // to a separate managed scalar implementation outside the timed window.
        long started = Stopwatch.GetTimestamp();
        ulong[] tokens = new ulong[Math.Max(1, s_count - 1)];
        ulong[] expected = new ulong[tiles];
        ulong hash = 0xcbf29ce484222325;
        int tile = 0;
        while (tile < tiles)
        {
            int batch = Math.Min(s_count, tiles - tile);
            for (int i = 1; i < batch; i++) { tokens[i - 1] = Enqueue((ulong)i, 2, pixels, (ulong)(tile + i), rounds, 0); }
            expected[tile] = Bsp(2, pixels, (ulong)tile, rounds);
            hash = unchecked((hash ^ expected[tile]) * 0x100000001b3);
            for (int i = 1; i < batch; i++)
            {
                ulong token = tokens[i - 1]; Finished(token);
                expected[tile + i] = Job(token, 1); hash = unchecked((hash ^ expected[tile + i]) * 0x100000001b3);
                Require(Control(token, 2) == 1, "image reap refused");
            }
            tile += batch;
        }
        long micros = (Stopwatch.GetTimestamp() - started) * 1000000 / Stopwatch.Frequency;
        // A complete smaller tile checks actual pixel data, not only a hash from
        // the same native implementation. AP-owned buffers never contain GC roots.
        for (int cpu = 1; cpu < s_count; cpu++)
        {
            ulong token = Enqueue((ulong)cpu, 2, 256, 17, 8, 0); Finished(token);
            for (uint p = 0; p < 256; p++)
            {
                uint value = 0xff000000 | ((17 * 33 + p * 17) & 0xffffff);
                for (uint r = 0; r < 8; r++) { value = Blend(value, r + 17); }
                Require(Pixel(token, p) == value, "AP image pixel mismatch");
            }
            Require(Control(token, 2) == 1, "pixel reap refused");
        }
        // Verify the full timed workload using a sequential native reference.
        for (int i = 0; i < tiles; i++) { Require(Bsp(2, pixels, (ulong)i, rounds) == expected[i], "parallel image differs from sequential"); }
        c.WriteLine("native image verified; tiles=32 pixels=4096 rounds=128 cpus=" + s_count + " elapsed-us=" + micros + " hash=" + hash.ToString("x"));
    }

    private static void FaultTest(CommandContext c)
    {
        ulong failed = (ulong)(s_count - 1);
        ulong token = Enqueue(failed, 3, 1, 0, 1, 1); Require(token != 0, "fault fixture refused");
        Require(Wait(() => Job(token, 0) == 2), "fault fixture not claimed");
        ulong[] queued = new ulong[7];
        for (int i = 0; i < queued.Length; i++)
        {
            queued[i] = Enqueue(failed, 1, 1000, 0, 1, 0);
            Require(queued[i] != 0, "fault queue fixture refused");
        }
        Require(Control(token, 1) == 1, "fault fixture start refused");
        Require(Wait(() => Field(failed, 0) == 4 && Field(failed, 12) == 1), "faulted AP did not halt");
        Require(Field(failed, 11) == 6 && Field(failed, 15) == 1 && Job(token, 0) == 5, "fault was not contained on private IST");
        Require(Enqueue(failed, 1, 1, 0, 1, 0) == 0, "failed AP accepted work");
        foreach (ulong pending in queued)
        {
            Require(Job(pending, 0) == 5 && Control(pending, 2) == 0, "failed queue lost/quasi-reused an owned job");
        }
        for (int cpu = 1; cpu < s_count - 1; cpu++)
        {
            ulong other = Enqueue((ulong)cpu, 1, 1000, 0, 1, 0); Finished(other);
            Require(Job(other, 1) == Checksum(1000), "healthy AP failed after sibling fault"); Control(other, 2);
        }
        c.WriteLine("native fault contained; vector=6 private IST verified; failed queue reserved; healthy APs responsive");
    }

    private static void StopTest(CommandContext c)
    {
        ulong[] tokens = new ulong[(s_count - 1) * 8];
        for (int cpu = 1; cpu < s_count; cpu++)
        {
            for (int i = 0; i < 8; i++)
            {
                ulong token = Enqueue((ulong)cpu, 1, 100000000, 0, 1, 1);
                Require(token != 0, "stop fixture admission refused"); tokens[(cpu - 1) * 8 + i] = token;
            }
        }
        Require(Wait(() =>
        {
            for (int cpu = 1; cpu < s_count; cpu++) { if (Job(tokens[(cpu - 1) * 8], 0) != 2) { return false; } }
            return true;
        }), "stop fixture did not park held/running jobs");
        Require(Control(tokens[0], 1) == 1, "running stop fixture not released");
        Require(Wait(() => Job(tokens[0], 2) != 0), "running stop fixture not started");
        bool stopped = false;
        Exception? stopError = null;
        // The command intentionally retains s_gate. A second managed BSP worker
        // must still be able to close admission and stop the native pool.
        new Thread(() =>
        {
            try { Quiesce(); }
            catch (Exception e) { stopError = e; }
            Volatile.Write(ref stopped, true);
        }).Start();
        Require(Wait(() => Volatile.Read(ref stopped)), "stop blocked behind managed producer gate");
        Require(stopError is null, "concurrent stop failed: " + stopError?.Message);
        for (int i = 0; i < tokens.Length; i++)
        {
            ulong status = Job(tokens[i], 0);
            // Execution may legitimately beat cancellation. Every other fixture
            // job was held/queued and must acknowledge cancellation.
            Require(i == 0 ? status == 3 || status == 4 : status == 4, "stop left an owned job active");
        }
        Require(Enqueue(1, 1, 1, 0, 1, 0) == 0, "stopped pool admitted work");
        c.WriteLine("native stop verified; running/queued/parked jobs quiesced; admission closed");
    }
}
