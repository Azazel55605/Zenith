using System;
using System.Diagnostics;
using System.Text;
using Zenith.Core.Network;
using Zenith.Core.Storage;
using Zenith.Core.Time;

namespace Zenith.Core.Processes;

/// <summary>Read-only bounded public metadata; never reads account secrets or
/// conflates the installed package catalogue with the kernel currently running.</summary>
internal static class NativeSystemInformation
{
    private static string _display = "No graphical session";
    public static void Display(string value) { _display = value; }
    public static byte[] Snapshot()
    {
        string generation = SystemMounts.Selection.KernelId ?? "unknown (live image)";
        return Encoding.UTF8.GetBytes("Zenith OS\n\nRuntime: Cosmos Gen 3 / NativeAOT\n"
            + "Running kernel generation:\n" + generation + "\n"
            + "Kernel release version: not recorded in the running image\n"
            + "Host: " + NetworkService.Hostname + "\n"
            + "Display: " + _display
            + "\nBoot: " + SystemMounts.Mode.ToString().ToLowerInvariant() + "\n"
            + "Uptime (seconds): " + Stopwatch.GetTimestamp() / Stopwatch.Frequency + "\n"
            + "Local time: " + SystemClock.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
            + "Time zone: " + SystemClock.Zone.Name + "\n"
            + "\nInstalled packages may differ from this running generation.");
    }
}
