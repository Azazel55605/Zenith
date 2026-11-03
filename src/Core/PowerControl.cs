using System;
using System.Threading;
using Cosmos.Kernel.System;
using Zenith.Core.Storage;

namespace Zenith.Core;

/// <summary>Power changes occur only after checked sync, unmount and disk flush.</summary>
internal static class PowerControl
{
    /// <summary>Why power cannot change right now, or null. Menus show this instead of failing on click.</summary>
    public static string? BlockedReason
        => Zenith.Core.Processes.NativeProcess.IsRunning ? "close running applications first"
            : Installer.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning
                ? "wait for installation or update to finish" : null;

    /// <summary>A last look on screen, given what is happening. Bounded, and only ever after the disks are safe.</summary>
    public static Action<string>? Farewell { get; set; }

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
            if (BlockedReason is string blocked) { throw new InvalidOperationException(blocked); }
            Log.Write("power", message);
#if !ARCH_ARM64
            // Logged before unmounting so boot.log.1 explains a failed power-off.
            if (powerOff) { Log.Write("power", Zenith.Core.Boot.NativeAcpiPower.Describe()); }
#endif
            Zenith.Core.Multiprocessing.CpuWorkers.Quiesce();
            SystemMounts.Shutdown();
            try { Farewell?.Invoke(powerOff ? "Shutting down" : "Restarting"); }
            catch (Exception e) { Log.Write("power", "farewell skipped: " + e.Message); }
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
