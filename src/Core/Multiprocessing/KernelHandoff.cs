using System.Runtime.InteropServices;
using System.Threading;

namespace Zenith.Core.Multiprocessing;

/// <summary>
/// The kernel loop is the scheduler's idle thread. When it wakes another thread (network data for a
/// download, an input event for a native app), that thread would otherwise wait for the next 10 ms
/// timer tick. Wakers call <see cref="Zenith.Core.Processes.WakeSignal.Request"/>; the loop then raises the scheduler's own interrupt on this
/// CPU so the woken thread runs at once.
/// </summary>
internal static class KernelHandoff
{

#if !ARCH_ARM64
    [DllImport("*", EntryPoint = "zenith_cpu_reschedule_self"), SuppressGCTransition]
    private static extern ulong Reschedule(ulong hhdm);
#endif

#if !ARCH_ARM64
    [DllImport("*", EntryPoint = "zenith_cpu_park_configure"), SuppressGCTransition]
    private static extern ulong ParkConfigure(ulong hhdm, ulong enabled);
#endif

    /// <summary>Whether a parked thread switches away at once (the adapted halt; see ParkSwitchFixup).</summary>
    public static bool ParkSwitch { get; private set; }

    /// <summary>Turns the park switch on or off; returns how many parks switched so far.</summary>
    public static unsafe ulong SetParkSwitch(bool enabled)
    {
#if !ARCH_ARM64
        var response = Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response;
        ParkSwitch = enabled && response != null;
        // All three scheduler rules: switch away from a parked thread, give the kernel loop a turn
        // after 6 ms, and hand queued threads the CPU when the loop would halt.
        return ParkConfigure(response == null ? 0 : response->Offset, ParkSwitch ? 7UL : 0UL);
#else
        return 0;
#endif
    }

    public static unsafe void Apply()
    {
        if (!Zenith.Core.Processes.WakeSignal.Take()) { return; }
#if !ARCH_ARM64
        var response = Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response;
        Reschedule(response == null ? 0 : response->Offset);
#endif
    }
}
