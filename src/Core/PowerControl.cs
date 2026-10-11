using Cosmos.Kernel.System;
using Zenith.Core.Storage;

namespace Zenith.Core;

/// <summary>Reboot and power-off that leave the disks consistent: everything is synced and unmounted first.</summary>
internal static class PowerControl
{
    public static void Reboot()
    {
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning) { throw new System.InvalidOperationException("wait for installation/update to finish before shutting down"); }
            Log.Write("power", "rebooting");
            SystemMounts.Shutdown();
            Power.Reboot();
        }
    }

    public static void PowerOff()
    {
        lock (Installer.MaintenanceGate)
        {
            if (Installer.IsRunning || Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Zenith.Core.Shell.Commands.PackageCommands.IsRunning) { throw new System.InvalidOperationException("wait for installation/update to finish before shutting down"); }
            Log.Write("power", "powering off");
            SystemMounts.Shutdown();
            Power.Shutdown();
        }
    }
}
