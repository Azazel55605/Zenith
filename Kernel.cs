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
            Zenith.Core.Multiprocessing.CpuWorkers.Initialize();
            SystemRuntime.Initialize();
            SessionRuntime.Start(SystemConfiguration.Settings.Current.DefaultSession,
                mode => mode switch
                {
                    SessionMode.Desktop => new DesktopSessionHost(),
                    SessionMode.Headless => new HeadlessSessionHost(),
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
                Stopwatch.GetTimestamp(), Stopwatch.Frequency))
            {
                ulong before = MemoryInfo.FreePages;
                MemoryInfo.Collect();
                Log.Write("memory", "pressure collection: free pages " + before + " -> " + MemoryInfo.FreePages);
            }

            Zenith.Core.Network.NetworkService.Tick();
            SessionRuntime.Tick();
        }
        catch (Exception e)
        {
            ReportPanic(e);
        }

        // Sleep until the next interrupt: a mouse or keyboard IRQ wakes the loop at once,
        // the timer tick at the latest, instead of polling on a fixed Thread.Sleep.
        Sys.Power.Halt();
    }

    private static void ReportPanic(Exception exception)
    {
        if (SessionRuntime.Started && SessionRuntime.Selected.Mode == SessionMode.Desktop)
        {
            try { PanicScreen.Show(exception); }
            catch (Exception drawing) { Log.Write("panic", "graphical reporting failed: " + drawing.Message); }
        }
        SerialPanic.Show(exception);
    }
}
