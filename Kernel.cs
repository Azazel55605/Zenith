using System;
using System.Diagnostics;
using Cosmos.Kernel.System.Graphics;
using Zenith.Apps;
using Zenith.Core;
using Zenith.Core.Input;
using Zenith.Core.Memory;
using Zenith.Core.Shell.Commands;
using Zenith.Core.Storage;
using Zenith.Core.Time;
using Zenith.Gui;
using Zenith.Gui.Graphics;
using Zenith.Gui.Shell;
using MemoryInfo = Cosmos.Kernel.System.Diagnostics.MemoryInfo;
using CommandShell = Zenith.Core.Shell.Shell;
using Sys = Cosmos.Kernel.System;

namespace Zenith;

public class Kernel : Sys.Kernel
{
    private Desktop _desktop = null!;
    private readonly MemoryPressurePolicy _memoryPressure = new();

    protected override void BeforeRun()
    {
        try
        {
            SystemMounts.Initialize();
            KeyboardLayouts.LoadSaved();
            if (SystemClock.LoadSaved() is string clockError)
            {
                Log.Write("clock", clockError);
            }
            SystemCommands.Register(CommandShell.Register);
            Fonts.Load();
            _desktop = new Desktop(Canvas.GetFullScreen());
            _desktop.Windows.Open(new WelcomeWindow());
            _desktop.Windows.Open(new TerminalWindow());
            Log.Write("kernel", "desktop ready (" + (SystemMounts.Mode == BootMode.Live ? "live" : "installed") + ")");
        }
        catch (Exception e)
        {
            PanicScreen.Show(e);
        }
    }

    protected override void Run()
    {
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

            _desktop.Tick();
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
