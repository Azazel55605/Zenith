using System;
using System.Threading;
using Cosmos.Kernel.System;
using Zenith.Core.Storage;

namespace Zenith.Core;

/// <summary>Power changes occur only after checked sync, unmount and disk flush.</summary>
internal static class PowerControl
{
    public static void Reboot() => Shutdown(Power.Reboot, "rebooting", false);
    public static void PowerOff() => Shutdown(FirmwarePowerOff, "powering off", true);

    private static void FirmwarePowerOff()
    {
#if !ARCH_ARM64
        Zenith.Core.Boot.NativeAcpiPower.PowerOff();
#endif
        Power.Shutdown();
    }

    private static void Shutdown(Action power, string message, bool powerOff)
    {
        Monitor.Enter(Installer.MaintenanceGate);
        try
        {
            if (Installer.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning || Zenith.Core.Processes.NativeProcess.IsRunning)
            { throw new InvalidOperationException("wait for installation/update to finish before shutting down"); }
            Log.Write("power", message);
#if !ARCH_ARM64
            // Logged before unmounting so boot.log.1 explains a failed power-off.
            if (powerOff) { Log.Write("power", Zenith.Core.Boot.NativeAcpiPower.Describe()); }
#endif
            SystemMounts.Shutdown();
            power();
        }
        catch
        {
            // Cosmos skips unwind-finally: a failed flush must release the gate
            // so the user can retry or inspect the failure without deadlocking.
            Monitor.Exit(Installer.MaintenanceGate);
            throw;
        }
        Monitor.Exit(Installer.MaintenanceGate);
    }
}
