using System;
using System.Diagnostics;
using Zenith.Core;
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

    protected override void BeforeRun()
    {
        using var credentials = CredentialContext.Enter(Credentials.Root);
        try
        {
            SystemRuntime.Initialize();
            SessionRuntime.Start(SystemConfiguration.Settings.Current.DefaultSession,
                mode => mode == SessionMode.Desktop ? new DesktopSessionHost()
                    : throw new InvalidOperationException("unavailable session host"));
        }
        catch (Exception e)
        {
            PanicScreen.Show(e);
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

            SessionRuntime.Tick();
        }
        catch (Exception e)
        {
            PanicScreen.Show(e);
        }

        // Sleep until the next interrupt: a mouse or keyboard IRQ wakes the loop at once,
        // the timer tick at the latest, instead of polling on a fixed Thread.Sleep.
        Sys.Power.Halt();
    }
}
