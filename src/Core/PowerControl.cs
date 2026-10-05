using Cosmos.Kernel.System;
using Zenith.Core.Storage;

namespace Zenith.Core;

/// <summary>Reboot and power-off that leave the disks consistent: everything is synced and unmounted first.</summary>
internal static class PowerControl
{
    public static void Reboot()
    {
        Log.Write("power", "rebooting");
        SystemMounts.Shutdown();
        Power.Reboot();
    }

    public static void PowerOff()
    {
        Log.Write("power", "powering off");
        SystemMounts.Shutdown();
        Power.Shutdown();
    }
}
