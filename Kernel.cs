using System;
using System.Diagnostics;
using Zenith.Core;
using Zenith.Core.Console;
using Zenith.Core.Configuration;
using Zenith.Core.Sessions;
using Zenith.Core.Security;
using Zenith.Core.Memory;
using Zenith.Gui;
using Zenith.Gui.Shell;
using MemoryInfo = Cosmos.Kernel.System.Diagnostics.MemoryInfo;
using Sys = Cosmos.Kernel.System;

namespace Zenith;

public class Kernel : Sys.Kernel
{
    private readonly MemoryPressurePolicy _memoryPressure = new();

    // Cosmos Global.Initialize only builds KernelConsole; HAL/managers are already up.
    // Select the graphical host after loading saved settings, without a bootstrap canvas.
    protected override void OnBoot() { }

    protected override void BeforeRun()
    {
        using var credentials = CredentialContext.Enter(Credentials.Root);
        try
        {
            bool hasDisplay = DesktopSessionHost.HasDisplay();
            SessionRuntime.DesktopAvailable = () => hasDisplay;
            SessionRuntime.LoginFactory = mode => new LoginSessionHost(mode);
            Zenith.Core.Multiprocessing.CpuWorkers.Initialize();
            Zenith.Core.Time.SystemClock.HardwareClockIsLocal = HardwareClockIsLocal(CommandLine());
            InitializePlatformClock(CommandLine());
            SessionDisplay.MarkSessionThread();
            PowerControl.Farewell = SessionDisplay.Farewell;
            bool bootScreen = hasDisplay && BootScreen.Wanted(CommandLine());
            if (bootScreen) { BootScreen.Begin(); }
            SystemRuntime.Initialize(bootScreen ? BootScreen.Phase : null);
            if (bootScreen) { BootScreen.Phase("Starting session"); BootScreen.Finish(); }
            SessionRuntime.Start(SystemConfiguration.Settings.Current.DefaultSession,
                mode => mode switch
                {
                    SessionMode.Desktop => new DesktopSessionHost(),
                    SessionMode.Headless => hasDisplay ? new LocalConsoleHost() : new HeadlessSessionHost(),
                    _ => throw new InvalidOperationException("unavailable session host")
                });
        }
        catch (Exception e)
        {
            ReportPanic(e);
        }
    }

    protected override void Run()
    {
        using var credentials = CredentialContext.Enter(Accounts.Session);
        try
        {
            if (KernelPanic.Requested is string reason)
            {
                throw new InvalidOperationException("panic requested: " + reason);
            }

            if (_memoryPressure.ShouldCollect(MemoryInfo.TotalPages, MemoryInfo.FreePages,
                Stopwatch.GetTimestamp(), Stopwatch.Frequency, (ulong)GC.GetTotalAllocatedBytes(false), MemoryInfo.TotalCollections))
            {
                ulong before = MemoryInfo.FreePages;
                MemoryInfo.Collect();
                Log.Write("memory", "pressure collection: free pages " + before + " -> " + MemoryInfo.FreePages);
            }

            Zenith.Core.Multiprocessing.ManagedCpuJobs.PumpRequests();
            Zenith.Core.Network.NetworkService.Tick();
            if (!SessionDisplay.ServeFarewell()) { SessionRuntime.Tick(); }
        }
        catch (Exception e)
        {
            ReportPanic(e);
        }

        // Sleep until the next interrupt: a mouse or keyboard IRQ wakes the loop at once,
        // the timer tick at the latest, instead of polling on a fixed Thread.Sleep.
        // While the desktop paces an animation or drag, run the loop again instead of waiting for
        // the next timer tick (10 ms), which capped frames and made motion feel slow.
        // Let a thread this pass woke (download, native app input) run now, not at the next tick.
        Zenith.Core.Multiprocessing.KernelHandoff.Apply();
        if (!SessionRuntime.FramePending) { Sys.Power.Halt(); }
    }

    private static void ReportPanic(Exception exception)
    {
        if (SessionRuntime.Started && SessionRuntime.Selected.Mode == SessionMode.Desktop)
        {
            try { PanicScreen.Show(exception); }
            catch (Exception drawing) { Log.Write("panic", "graphical reporting failed: " + drawing.Message); }
        }
        // Before the first session only the boot screen is showing: it must not hide the failure.
        else if (BootScreen.Shown)
        {
            try { PanicScreen.Paint(exception); }
            catch (Exception drawing) { Log.Write("panic", "graphical reporting failed: " + drawing.Message); }
        }
        SerialPanic.Show(exception);
    }

    /// <summary>RTC basis is explicit; hypervisor identity is not a time zone.
    /// VMware's separate host UTC clock is preferred unless an RTC override is supplied.</summary>
    private static bool HardwareClockIsLocal(string arguments)
    {
        foreach (string word in arguments.Split(' '))
        {
            if (word == "rtc=local") { return true; }
            if (word == "rtc=utc") { return false; }
        }
#if !ARCH_ARM64
        return false; // Hypervisor identity does not specify the RTC's time zone.
#else
        return false;
#endif
    }

#if !ARCH_ARM64
    [System.Runtime.InteropServices.DllImport("*", EntryPoint = "zenith_vmware_utc_seconds"), System.Runtime.InteropServices.SuppressGCTransition]
    private static extern ulong VmwareUtcSeconds();
#endif
    private static void InitializePlatformClock(string arguments)
    {
#if !ARCH_ARM64
        foreach (string word in arguments.Split(' '))
        { if (word is "rtc=utc" or "rtc=local") { return; } }
        ulong seconds = VmwareUtcSeconds();
        if (seconds != 0)
        {
            Zenith.Core.Time.SystemClock.Synchronize(DateTime.UnixEpoch.AddSeconds(seconds));
            Zenith.Core.Time.SystemClock.Source = "VMware host UTC (unauthenticated)";
        }
#endif
    }

    private static unsafe string CommandLine()
        => System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)Cosmos.Kernel.Boot.Limine.Limine.Cmdline) ?? "";
}
