using System.Threading;

namespace Zenith.Core.Multiprocessing;

internal static partial class CpuWorkers
{
    // Slice waits. A service thread used to halt on the BSP while its slice ran on an AP, still
    // Running: the kernel loop (the desktop) is the scheduler's idle thread and could not run
    // until a timer tick took the CPU away, up to 10 ms per program call. With the park switch
    // (KernelHandoff) the thread instead parks on a monitor and the CPU goes to the next thread
    // at once. Every slice completion rings the BSP doorbell; the kernel loop pulses the waiters
    // when it sees a new completion, and the timeout bounds a missed pulse.
    private static readonly object s_sliceGate = new();
    private static ulong s_slicePulsed;
    public static bool BlockingSliceWaits = true;
    public static long SliceParks, SlicePulses;

    private static void WaitForSlice(ulong token)
    {
        if (!BlockingSliceWaits || !KernelHandoff.ParkSwitch)
        {
            if (ProcessWait(token) == 0) Thread.Sleep(1);
            return;
        }
        lock (s_sliceGate)
        {
            // A completion after this check changes the doorbell count; the kernel loop can only
            // announce it under this gate, which Wait releases.
            if (ProcessField(token,0) >= 3) return;
            SliceParks++;
            Monitor.Wait(s_sliceGate, 10);
        }
    }

    /// <summary>On the kernel loop: wakes slice waiters after a completion rang the doorbell.</summary>
    public static void PulseSliceWaiters()
    {
        if (s_count < 2) return;
        ulong rung = Field(0, 25);
        if (rung == Volatile.Read(ref s_slicePulsed) || !Monitor.TryEnter(s_sliceGate)) return;
        try
        {
            s_slicePulsed = rung;
            SlicePulses++;
            Monitor.PulseAll(s_sliceGate);
        }
        finally { Monitor.Exit(s_sliceGate); }
        Zenith.Core.Processes.WakeSignal.Request();
    }
}
