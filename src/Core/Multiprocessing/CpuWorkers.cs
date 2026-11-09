using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Boot.Limine;
using Zenith.Core.Security;
using Zenith.Core.Shell;

namespace Zenith.Core.Multiprocessing;

/// <summary>C1 native-only AP queues; all managed callers/GC/UI ownership stay on BSP.</summary>
internal static partial class CpuWorkers
{
    private static readonly object s_gate = new();
    private static int s_count;
    private static ulong[]? s_heldManaged;
    private static volatile bool s_stopping;
    [DllImport("*", EntryPoint = "zenith_cpu_detected"), SuppressGCTransition]
    private static extern ulong Detected();
    [DllImport("*", EntryPoint = "zenith_cpu_idle_ticks"), SuppressGCTransition]
    private static extern ulong IdleTicksNative(ulong cpu);
    [DllImport("*", EntryPoint = "zenith_cpu_now"), SuppressGCTransition]
    private static extern ulong NowNative();
    /// <summary>Logical CPUs doing work: the BSP alone unless smp=native started workers.</summary>
    public static int Online => Math.Max(1, s_count);
    /// <summary>Logical CPUs firmware reported, used or not.</summary>
    public static int DetectedCpus => (int)Math.Min(64UL, Detected());
    /// <summary>A worker that faulted or stopped is out of service (always false for the BSP).</summary>
    public static bool OutOfService(int cpu) => cpu > 0 && (cpu >= s_count || Field((ulong)cpu, 11) != 0 || Field((ulong)cpu, 0) >= 4);
    /// <summary>TSC ticks this CPU spent halted, and the TSC now (same clock on every CPU).</summary>
    public static ulong IdleTicks(int cpu) => IdleTicksNative((ulong)cpu);
    public static ulong Now => NowNative();
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

    [DllImport("*", EntryPoint = "zenith_cpu_reschedule"), SuppressGCTransition]
    private static extern ulong Reschedule(ulong cpu);

    [DllImport("*", EntryPoint = "zenith_execution_fp_preempt_test"), SuppressGCTransition]
    private static extern ulong FpPreemptTest();

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
            ManagedCpuJobs.PumpRequests();
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

    [DllImport("*", EntryPoint = "zenith_cpu_wait_interrupt"), SuppressGCTransition]
    private static extern ulong WaitInterrupt(ulong token);

    private static void Finished(ulong token, ulong expected = 3, bool shortCompute = false)
    {
        Require(token != 0, "native queue admission refused");
        if (shortCompute)
        {
            long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            while (Job(token, 0) < 3 && Stopwatch.GetTimestamp() < deadline)
            {
                ManagedCpuJobs.PumpRequests();
                if (WaitInterrupt(token) == 0) Thread.Sleep(1);
            }
            Require(Job(token, 0) >= 3, "short compute completion timeout");
        }
        else Require(Wait(() => Job(token, 0) >= 3), "native job completion timeout");
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
            c.WriteLine("detected=" + Detected() + " native-workers=" + s_count + " stopping=" + s_stopping + " managed-cpus=1 pinned-ap-owners=" + (ManagedCpuJobs.Ready ? Math.Max(0, s_count - 1) : 0));
            c.WriteLine("initial-idt-interrupts-enabled=" + ((InitialIdtFlags() & 0x200) != 0));
            c.WriteLine("Experimental workers require boot argument smp=native; idle=interrupt queue-capacity=8.");
            for (int i = 1; i < s_count; i++)
            {
                ulong x = (ulong)i;
                c.WriteLine("cpu=" + i + " apic-mode=" + Field(x, 18) + " state=" + Field(x, 0) + " parks=" + Field(x, 8) + " wakes=" + Field(x, 9)
                    + " completed=" + Field(x, 10) + " fault=" + Field(x, 11) + " irq-stack-errors=" + Field(x, 16) + " ipi-errors=" + Field(x, 17)
                    + (Field(x, 0) >= 4 ? " fault-rip=0x" + Field(x, 30).ToString("x") + " fault-code=0x" + Field(x, 31).ToString("x") + " fault-cr2=0x" + Field(x, 32).ToString("x") + " fault-stack-ok=" + Field(x, 15) : ""));
            }
            return 0;
        }
        if (c.Args[0] == "park" && c.Args.Length <= 2)
        {
            if (c.Args.Length == 2)
            {
                GuardedFilesystem.RequireRoot();
                if (c.Args[1] is not ("on" or "off")) { return c.Fail("usage: cpu park [on|off]"); }
                KernelHandoff.SetParkSwitch(c.Args[1] == "on");
            }
            c.WriteLine("park-switch=" + KernelHandoff.ParkSwitch + " switches=" + KernelHandoff.SetParkSwitch(KernelHandoff.ParkSwitch)
                + " slice-parks=" + SliceParks + " slice-pulses=" + SlicePulses + " doorbell=" + (s_count > 0 ? Field(0, 25) : 0));
            return 0;
        }
        GuardedFilesystem.RequireRoot();
        if (c.Args.Length == 1 && c.Args[0] == "fp-test")
        {
            bool ok = FpPreemptTest() == 1;
            c.WriteLine("FP interrupt preemption x87/MXCSR/XMM=" + ok);
            return ok ? 0 : 1;
        }
        if (c.Args.Length == 1 && c.Args[0] == "irq-test")
        {
            int result = IdtIrqTest();
            c.WriteLine("IDT interrupt-state preservation failures=" + result);
            return result == 0 ? 0 : 1;
        }
        if (c.Args.Length > 2 || (c.Args.Length == 2 && c.Args[0] != "test")) { return c.Fail(Usage); }
        string action = c.Args[0];
        if (action != "blur-test" && action != "test" && action != "cancel-test" && action != "queue-test" && action != "idle-test"
            && action != "managed-test" && action != "managed-fault-test" && action != "managed-image-test" && action != "managed-hold" && action != "managed-hold-one" && action != "managed-release" && action != "state-test" && action != "image-test" && action != "fault-test" && action != "stop-test") { return c.Fail(Usage); }
        ulong iterations = 2000000;
        if (c.Args.Length == 2 && (!ulong.TryParse(c.Args[1], out iterations) || iterations < 1 || iterations > 10000000))
        { return c.Fail("iterations must be 1..10000000"); }
        Monitor.Enter(s_gate);
        int status;
        try
        {
            if (s_count == 0 || s_stopping || (s_count < 2 && action != "blur-test" && action != "image-test" && action != "managed-image-test")) { status = c.Fail("native workers unavailable; boot with smp=native"); }
            else
            {
                switch (action)
                {
                    case "blur-test": BlurTest(c); break;
                    case "managed-hold": ManagedHold(c); break;
                    case "managed-hold-one": ManagedHold(c, true); break;
                    case "managed-release": ManagedRelease(c); break;
                    case "managed-image-test": ManagedImageTest(c); break;
                    case "managed-fault-test": ManagedFaultTest(c); break;
                    case "managed-test": ManagedTest(c); break;
                    case "state-test": StateTest(c); break;
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

    private const string Usage = "cpu status | blur-test | irq-test | fp-test | managed-test | managed-fault-test | managed-image-test | managed-hold | managed-release | test [iterations] | cancel-test | state-test | queue-test | idle-test | image-test | fault-test | stop-test";

    // A live protected application may briefly retain this AP. Retry only a
    // refused admission (token zero), never an accepted operation. Production
    // queues retain their nonblocking busy/fallback semantics.
    private static ulong DiagnosticEnqueue(ulong cpu, ulong kind, ulong count, ulong seed, ulong rounds, ulong held)
    {
        ulong token = 0;
        Require(Wait(() => (token = Enqueue(cpu, kind, count, seed, rounds, held)) != 0),
            "diagnostic queue admission unavailable");
        return token;
    }

    private static void Batch(CommandContext c, ulong n, bool cancel)
    {
        ulong[] tokens = new ulong[s_count];
        for (int i = 1; i < s_count; i++) { tokens[i] = DiagnosticEnqueue((ulong)i, 1, n, 0, 1, 1); Require(tokens[i] != 0, "batch refused"); }
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

    private static void ManagedHold(CommandContext c, bool one = false)
    {
        Require(s_heldManaged is null, "managed diagnostic batch already held");
        ManagedCpuJobs.Initialize((ulong)s_count);
        var tokens = new ulong[one ? Math.Min(2,s_count) : s_count]; s_heldManaged = tokens;
        for (int cpu = 1; cpu < tokens.Length; cpu++)
        {
            tokens[cpu] = Enqueue((ulong)cpu, 5, 4096, 17, 1, 0);
            Require(tokens[cpu] != 0, "managed hold admission");
        }
        Require(Wait(() =>
        {
            for (int cpu = 1; cpu < tokens.Length; cpu++) if (Job(tokens[cpu], 4) != 100) return false;
            return true;
        }), "managed hold readiness");
        c.WriteLine("managed roots parked on APs; BSP services remain available");
    }

    private static void ManagedRelease(CommandContext c)
    {
        ulong[] tokens = s_heldManaged ?? throw new InvalidOperationException("no managed diagnostic batch held");
        for (int cpu = 1; cpu < tokens.Length; cpu++) Require(Control(tokens[cpu], 3) == 1, "managed hold release");
        ulong expected = 0xcbf29ce484222325UL;
        for (ulong i = 0; i < 4096; i++) expected = (expected ^ (byte)(17 + i)) * 0x100000001b3UL;
        for (int cpu = 1; cpu < tokens.Length; cpu++)
        {
            Finished(tokens[cpu]); Require(Job(tokens[cpu], 1) == expected, "managed roots survived BSP service pressure");
            Require(Control(tokens[cpu], 2) == 1, "managed hold reap");
        }
        s_heldManaged = null; c.WriteLine("managed AP roots survived BSP services; owners reusable");
    }

    /// <summary>Trusted UI-owner diagnostic. APs return complete private tiles;
    /// only the BSP stitches and hands the completed frame to a window.</summary>
    internal static uint[] RenderManagedPreview(out int cpus, out long micros)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("managed CPU preview requires x86_64");
        if (!Monitor.TryEnter(s_gate)) throw new InvalidOperationException("CPU diagnostics are busy");
        if (s_count == 0 || s_stopping || s_heldManaged is not null)
        {
            Monitor.Exit(s_gate);
            throw new InvalidOperationException("preview requires idle smp=native workers");
        }
        try
        {
            ManagedCpuJobs.Initialize((ulong)s_count);
            cpus = Math.Min(4, s_count);
            uint[] frame = new uint[128 * 128]; ulong[] tokens = new ulong[cpus];
            long start = Stopwatch.GetTimestamp();
            for (int tile = 0; tile < 4;)
            {
                int batch = Math.Min(cpus, 4 - tile);
                for (int i = 1; i < batch; i++)
                {
                    tokens[i] = Enqueue((ulong)i, 5, 4096, (ulong)(tile + i), 4, 0);
                    Require(tokens[i] != 0, "preview tile admission");
                }
                for (int i = 0; i < batch; i++)
                {
                    uint[] pixels;
                    if (i == 0) pixels = ManagedCpuJobs.BuildImage(4096, (uint)tile);
                    else
                    {
                        Finished(tokens[i], shortCompute: true);
                        pixels = new uint[4096]; ManagedCpuJobs.CopyFrame(tokens[i], pixels);
                        Require(Control(tokens[i], 2) == 1, "preview tile reap");
                    }
                    int index = tile + i, x = (index % 2) * 64, y = (index / 2) * 64;
                    for (int row = 0; row < 64; row++) Array.Copy(pixels, row * 64, frame, (y + row) * 128 + x, 64);
                }
                tile += batch;
            }
            micros = (Stopwatch.GetTimestamp() - start) * 1000000 / Stopwatch.Frequency;
            Monitor.Exit(s_gate); return frame;
        }
        catch
        {
            // Preserve accepted descriptor ownership on an unexpected failure.
            s_stopping = true; StopNative(); Monitor.Exit(s_gate); throw;
        }
    }

    private static void ManagedImageTest(CommandContext c)
    {
        ManagedCpuJobs.Initialize((ulong)s_count);
        const int tiles = 12, pixels = 4096;
        // Warm the full-sized path equally before timing. Multi-CPU safety
        // checks must not give only those cases a translated/allocated head start.
        _ = ManagedCpuJobs.BuildImage(pixels, 0);
        uint[] warm = new uint[pixels];
        for (int cpu = 1; cpu < s_count; cpu++)
        {
            ulong token = Enqueue((ulong)cpu, 5, pixels, 0, 4, 0);
            Finished(token, shortCompute: true); ManagedCpuJobs.CopyFrame(token, warm);
            Require(Control(token, 2) == 1, "managed warm-up reap");
        }
        CollectManaged();
        uint[][] frames = new uint[tiles][];
        ulong[] tokens = new ulong[s_count];
        ulong[] hashes = new ulong[tiles];
        ulong signals = Field(0, 25);
        long start = Stopwatch.GetTimestamp();
        for (int tile = 0; tile < tiles;)
        {
            int batch = Math.Min(s_count, tiles - tile);
            for (int i = 1; i < batch; i++)
            {
                tokens[i] = Enqueue((ulong)i, 5, pixels, (ulong)(tile + i), 4, 0);
                Require(tokens[i] != 0, "managed image admission");
            }
            frames[tile] = ManagedCpuJobs.BuildImage(pixels, (uint)tile);
            for (int i = 1; i < batch; i++)
            {
                ulong token = tokens[i]; Finished(token, shortCompute: true);
                hashes[tile + i] = Job(token, 1);
                frames[tile + i] = new uint[pixels]; ManagedCpuJobs.CopyFrame(token, frames[tile + i]);
                Require(Control(token, 2) == 1, "managed frame reap");
            }
            tile += batch;
        }
        long micros = (Stopwatch.GetTimestamp() - start) * 1000000 / Stopwatch.Frequency;
        ulong hash = 0xcbf29ce484222325UL;
        for (uint tile = 0; tile < tiles; tile++)
        {
            ulong tileHash = 0xcbf29ce484222325UL;
            for (uint p = 0; p < pixels; p++)
            {
                uint value = 0xff000000 | ((tile * 33 + p * 17) & 0xffffff);
                for (uint r = 0; r < 32; r++) value = Blend(value, r + tile);
                Require(frames[tile][p] == value, "managed complete-frame pixel mismatch");
                tileHash = (tileHash ^ value) * 0x100000001b3UL;
            }
            Require(hashes[tile] == 0 || hashes[tile] == tileHash, "managed frame checksum mismatch");
            hash = (hash ^ tileHash) * 0x100000001b3UL;
        }
        Require(s_count == 1 || Field(0, 25) > signals, "BSP native completion interrupt acknowledgement");
        c.WriteLine("managed image verified; tiles=12 pixels=4096 rounds=32 cpus=" + s_count + " elapsed-us=" + micros + " hash=" + hash.ToString("x"));
    }

    private static void ManagedFaultTest(CommandContext c)
    {
        ManagedCpuJobs.Initialize((ulong)s_count);
        ulong cpu = (ulong)(s_count - 1), token = Enqueue(cpu, 5, 1, 17, 6, 0);
        Require(token != 0 && Wait(() => Job(token, 4) == 100), "managed fault checkpoint");
        GC.Collect(); Require(Control(token, 3) == 1, "managed fault release");
        Require(Wait(() => Field(cpu, 12) == 1 && Job(token, 0) == 5), "managed fault containment");
        Require(Field(cpu, 11) == 6 && Field(cpu, 15) == 1, "managed fault private IST");
        ulong epochs = ManagedCpuJobs.State(2), refused = ManagedCpuJobs.State(3);
        byte[] survivor = new byte[1024]; survivor[42] = 117;
        GC.Collect();
        Require(ManagedCpuJobs.State(2) == epochs && ManagedCpuJobs.State(3) > refused && survivor[42] == 117,
            "missing managed AP acknowledgement must refuse collection before sweep");
        Require(Enqueue(cpu, 5, 1, 17, 2, 0) == 0 && Control(token, 2) == 0, "faulted managed storage quarantined");
        for (int other = 1; other < s_count - 1; other++)
        {
            ulong healthy = Enqueue((ulong)other, 5, 1, 17, 2, 0); Finished(healthy);
            Require(Job(healthy, 1) == (17UL ^ ((ulong)other << 32)), "healthy managed owner after sibling fault");
            Require(Control(healthy, 2) == 1, "healthy managed result reap");
        }
        c.WriteLine("managed AP fault quarantined; missing roots refuse collection without sweeping; BSP and siblings available");
    }

    private static void CollectManaged()
    {
        ulong before=ManagedCpuJobs.State(2),refused=ManagedCpuJobs.State(3);
        GC.Collect();
        Require(ManagedCpuJobs.State(2)>before&&ManagedCpuJobs.State(3)==refused,
            "healthy managed collection must actually stop/scan/resume every participant");
    }

    private static void ManagedTest(CommandContext c)
    {
        ManagedCpuJobs.Initialize((ulong)s_count);
        ulong deferred = ManagedCpuJobs.State(4);
        ulong[] tokens = new ulong[s_count];
        for (ulong cpu = 1; cpu < (ulong)s_count; cpu++)
        {
            tokens[cpu] = DiagnosticEnqueue(cpu, 5, 4096, 17, 3, 1);
            Require(tokens[cpu] != 0, "pinned managed job admission");
        }
        for (int cpu = 1; cpu < s_count; cpu++) Require(Control(tokens[cpu], 1) == 1, "pinned managed job release");
        Require(Wait(() =>
        {
            for (int cpu = 1; cpu < s_count; cpu++) if (Job(tokens[cpu], 4) != 100) return false;
            return true;
        }), "managed retained-root checkpoints; phase=" + Job(tokens[1], 4) + " state=" + Job(tokens[1], 0) + " result=" + Job(tokens[1], 1) + " fault=" + Field(1, 11));
        Require(ManagedCpuJobs.State(4) >= deferred + (ulong)(s_count - 1), "AP collection requests deferred to BSP");
        Require(ManagedCpuJobs.State(6) == ((1UL << s_count) - 2), "managed AP participant identity");
        for (int pass = 0; pass < 4; pass++) { CollectManaged(); Thread.Sleep(1); }
        for (int cpu = 1; cpu < s_count; cpu++) Require(Control(tokens[cpu], 3) == 1, "managed checkpoint release");
        for (int pass = 0; pass < 8; pass++) { CollectManaged(); Thread.Sleep(1); }
        ulong expected = 0xcbf29ce484222325UL;
        for (ulong i = 0; i < 4096; i++) expected = (expected ^ (byte)(17 + i)) * 0x100000001b3UL;
        for (int cpu = 1; cpu < s_count; cpu++)
        {
            ulong token = tokens[cpu]; Require(Wait(() => Job(token, 0) >= 3), "pinned managed completion");
            Require(Job(token, 0) == 3 && Job(token, 1) == expected, "pinned managed roots/checksum/TLS/credentials");
            Require(Control(token, 2) == 1, "pinned managed reap");
        }
        // Contained and reported exceptions must leave each permanent owner
        // reusable, without borrowing BSP credentials or another AP's EH chain.
        for (ulong operation = 2; operation <= 5; operation += 3)
        {
            for (int cpu = 1; cpu < s_count; cpu++) tokens[cpu] = DiagnosticEnqueue((ulong)cpu, 5, 1, (ulong)(31 + cpu), operation, 0);
            for (int cpu = 1; cpu < s_count; cpu++)
            {
                ulong token = tokens[cpu]; Require(token != 0 && Wait(() => Job(token, 0) >= 3), "managed exception completion");
                Require(operation == 2 ? Job(token, 0) == 3 && Job(token, 1) == ((ulong)(31 + cpu) ^ ((ulong)cpu << 32))
                    : Job(token, 0) == 5 && Job(token, 1) == ulong.MaxValue, "managed per-owner exception state");
                Require(Control(token, 2) == 1, "managed exception reap");
            }
        }
        ulong signals = Field(0, 25), fpToken = DiagnosticEnqueue(1, 5, 4096, 17, 4, 0);
        Require(fpToken != 0 && FpPreemptTest() == 1, "BSP FP state through native completion doorbell");
        Finished(fpToken); Require(Field(0, 25) > signals, "completion IPI delivery during FP probe");
        Require(Control(fpToken, 2) == 1, "FP completion probe reap");
        for (int cpu = 1; cpu < s_count; cpu++) tokens[cpu] = DiagnosticEnqueue((ulong)cpu, 5, 4096, 17, 1, 0);
        Require(Wait(() =>
        {
            for (int cpu = 1; cpu < s_count; cpu++) if (Job(tokens[cpu], 4) != 100) return false;
            return true;
        }), "managed cancellation checkpoints");
        GC.Collect();
        for (int cpu = 1; cpu < s_count; cpu++) Require(Control(tokens[cpu], 0) == 1, "managed cancellation request");
        for (int cpu = 1; cpu < s_count; cpu++)
        {
            ulong token = tokens[cpu]; Require(Wait(() => Job(token, 0) >= 3) && Job(token, 0) == 4, "managed cancellation acknowledgement");
            Require(Control(token, 2) == 1, "managed cancellation reap");
        }
        GC.Collect(); Require(ManagedCpuJobs.State(6) == 0, "managed participants retired");
        c.WriteLine("pinned managed allocation, stopped roots, TLS and identity verified; gc-epochs=" + ManagedCpuJobs.State(2));
        c.WriteLine("managed pinned/interior roots, deferred collection, exceptions and cancellation verified");
    }

    private static void StateTest(CommandContext c)
    {
        for (ulong cpu = 1; cpu < (ulong)s_count; cpu++)
        {
            ulong token = Enqueue(cpu, 4, 1, 0, 1, 0);
            Require(token != 0, "execution-state job admission");
            Require(Wait(() => Job(token, 0) >= 3), "execution-state completion");
            Require(Job(token, 0) == 3 && Job(token, 1) == ((cpu << 32) | 1), "CPU-local identity/switch and FP interrupt state");
            Require(Field(cpu, 19) > 0, "AP-local one-shot timer delivery");
            ulong before = Field(cpu, 20);
            Require(Reschedule(cpu) == 1, "targeted native reschedule IPI admission");
            Require(Wait(() => Field(cpu, 20) == before + 1), "targeted native reschedule IPI acknowledgement");
            Require(Control(token, 2) == 1, "execution-state result ownership");
        }
        c.WriteLine("CPU-local switch slots and FP interrupt state verified; managed CPUs=1");
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
