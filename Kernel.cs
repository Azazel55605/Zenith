using Cosmos.Kernel.System.Graphics;
using Zenith.Apps;
using Zenith.Core;
using Zenith.Core.Storage;
using Zenith.Gui.Graphics;
using Zenith.Gui.Shell;
using Sys = Cosmos.Kernel.System;

namespace Zenith;

public class Kernel : Sys.Kernel
{
    private Desktop _desktop = null!;

    protected override void BeforeRun()
    {
        SystemMounts.Initialize();
        Fonts.Load();
        _desktop = new Desktop(Canvas.GetFullScreen());
        _desktop.Windows.Open(new WelcomeWindow());
        _desktop.Windows.Open(new TerminalWindow());
        Log.Write("kernel", "desktop ready (" + (SystemMounts.Mode == BootMode.Live ? "live" : "installed") + ")");
    }

    protected override void Run()
    {
        _desktop.Tick();

        // Sleep until the next interrupt: a mouse or keyboard IRQ wakes the loop at once,
        // the timer tick at the latest, instead of polling on a fixed Thread.Sleep.
        Sys.Power.Halt();
    }
}
