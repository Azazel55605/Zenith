using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cosmos.Kernel.Boot.Limine;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Vfs;

namespace Zenith.Core.Storage.Proc;

internal static class ProcMount
{
    public static void Initialize()
    {
        var filesystem = new ProcFilesystemType(Memory, Mounts, Uptime, CommandLine);
        if (!VfsManager.RegisterFilesystem("proc", filesystem)
            || !VfsManager.TryMount("proc", "", MountFlags.ReadOnly | MountFlags.NoExec | MountFlags.NoDev, "/proc", out _))
        {
            throw new InvalidOperationException("Failed to mount /proc");
        }
        Log.Write("mounts", "proc mounted at /proc (read-only)");
    }

    private static string Memory()
    {
        const ulong KilobytesPerPage = 4;
        return "MemTotal: " + MemoryInfo.TotalPages * KilobytesPerPage + " kB\n"
            + "MemFree: " + MemoryInfo.FreePages * KilobytesPerPage + " kB\n";
    }

    private static string Uptime()
    {
        double seconds = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        // Cosmos exposes elapsed time but no aggregate CPU idle counter.
        return seconds.ToString("F2", CultureInfo.InvariantCulture) + "\n";
    }

    private static string Mounts()
    {
        var text = new StringBuilder();
        // Cosmos does not retain mount flags; proc is read-only; current dev and FAT mounts are writable.
        foreach (VfsManager.VfsMount mount in VfsManager.Mounts)
        {
            text.Append(Escape(mount.Partition?.Name ?? mount.Name)).Append(' ')
                .Append(Escape(mount.MountPoint)).Append(' ').Append(Escape(mount.Name)).Append(' ')
                .Append(mount.Name == "proc" ? "ro" : "rw").Append(" 0 0\n");
        }
        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\134").Replace(" ", "\\040").Replace("\t", "\\011").Replace("\n", "\\012");

    private static unsafe string CommandLine() => (Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "") + "\n";
}
