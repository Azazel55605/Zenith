using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zenith.Core.Multiprocessing;

internal static partial class CpuWorkers
{
    [DllImport("*", EntryPoint="zenith_cpu_process_submit"), SuppressGCTransition]
    private static extern ulong ProcessSubmit(ulong context, ulong affinity);
    [DllImport("*", EntryPoint="zenith_cpu_process_field"), SuppressGCTransition]
    private static extern ulong ProcessField(ulong token, ulong field);
    [DllImport("*", EntryPoint="zenith_cpu_process_control"), SuppressGCTransition]
    private static extern ulong ProcessControl(ulong token, ulong action);
    [DllImport("*", EntryPoint="zenith_cpu_process_mask"), SuppressGCTransition]
    private static extern ulong ProcessMask();
    [DllImport("*", EntryPoint="zenith_cpu_process_wait"), SuppressGCTransition]
    private static extern ulong ProcessWait(ulong token);
    [DllImport("*", EntryPoint="zenith_gc_fail_stop"), SuppressGCTransition]
    private static extern void ProcessFailStop();

    /// <summary>One protected context owns a raw slice until terminal publication.
    /// Managed service execution never leaves the BSP. -1 requests the serialized
    /// BSP bridge only when its bit is explicitly allowed by this invocation.</summary>
    internal static int RunProcessSlice(ulong context, uint affinity, Func<bool> cancelled, out int cpu)
    {
        cpu = -1;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return 95;
        if (s_stopping || cancelled()) return 130;
        if (s_count < 2 || (affinity & 254) == 0) return (affinity & 1) != 0 ? -1 : 95;
        ulong token;
        long admission = Stopwatch.GetTimestamp() + 120 * Stopwatch.Frequency;
        while ((token = ProcessSubmit(context,affinity & 254)) == 0)
        {
            if (s_stopping || cancelled()) return 130;
            if ((affinity & 1) != 0) return -1;
            if ((ProcessMask() & affinity) == 0) return 95;
            if (Stopwatch.GetTimestamp() >= admission) return 124; // No operation was admitted.
            Thread.Sleep(1);
        }
        long deadline = Stopwatch.GetTimestamp() + 120 * Stopwatch.Frequency;
        bool stopping = false;
        try
        {
            while (true)
            {
                ulong state = ProcessField(token,0);
                if (state == 6)
                {
                    Log.Write("cpu","protected process queue damaged; retaining invocation storage");
                    ProcessFailStop(); throw new InvalidOperationException("native process queue damaged");
                }
                if (state >= 3)
                {
                    Thread.MemoryBarrier(); cpu = (int)ProcessField(token,2); if (cpu == 0) cpu = -1;
                    int result = state == 4 ? 130 : state == 5 ? 139 : (int)ProcessField(token,1);
                    // A faulted owner remains quarantined. Its terminal/halted
                    // witness permits invocation cleanup, never reuse of that CPU.
                    ReapProcess(token,state);
                    return result;
                }
                if (!stopping && (s_stopping || cancelled() || Stopwatch.GetTimestamp() >= deadline))
                {
                    ProcessControl(token,0); stopping = true;
                    deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                }
                if (stopping && Stopwatch.GetTimestamp() >= deadline)
                {
                    // Returning/throwing would release this caller's stack Context
                    // and pages while a CPU might still dereference them. Fail-stop.
                    Log.Write("cpu","unacknowledged protected process stop; retaining invocation storage");
                    ProcessFailStop(); throw new InvalidOperationException("native process stop acknowledgement unavailable");
                }
                ManagedCpuJobs.PumpRequests();
                if (ProcessWait(token) == 0) Thread.Sleep(1);
            }
        }
        catch
        {
            // A callback/collection exception must not unwind the borrowed
            // Context stack while the AP still holds it.
            ProcessControl(token,0);
            long limit = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            while (ProcessField(token,0) < 3 && Stopwatch.GetTimestamp() < limit) ProcessWait(token);
            ulong state = ProcessField(token,0);
            if (state is < 3 or > 5) ProcessFailStop();
            ReapProcess(token,state);
            throw;
        }
    }

    private static void ReapProcess(ulong token, ulong state)
    {
        if (state == 5) return; // Quarantined storage is never reused.
        long limit = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (ProcessControl(token,2) == 0)
        {
            if (Stopwatch.GetTimestamp() >= limit) ProcessFailStop();
            Thread.Sleep(1);
        }
    }
}
