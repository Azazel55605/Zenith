using System;
using System.Diagnostics;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Packages;

/// <summary>
/// The periodic package cache cleanup: two minutes after boot, then every six hours, archives older
/// than a day and stages older than an hour leave the cache (<see cref="PackageCachePolicy"/>). The
/// kernel loop only checks the time; the work runs on its own thread, because the loop is the
/// scheduler's idle thread and must never wait for the package gate, credentials or filesystem
/// locks that program threads hold (that stalled the desktop under load in 0.0.52's validation).
/// It runs only when the package service is idle (a busy service just waits for the next turn),
/// as root, on an installed ext2 root, and never when automatic cleanup is off. A failure is
/// logged; it never blocks the session.
/// </summary>
internal static class PackageMaintenance
{
    private const long FirstSeconds = 120, IntervalSeconds = 6 * 60 * 60, RetrySeconds = 10 * 60;
    private static long s_due = -1;
    private static int s_running;

    /// <summary>On the kernel loop: no locks, no I/O; starts the cleanup thread when it is due.</summary>
    public static void Tick()
    {
        long now = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        if (s_due < 0) { s_due = now + FirstSeconds; return; }
        if (now < s_due) { return; }
        s_due = now + RetrySeconds;
        if (SystemMounts.Mode != BootMode.Installed) { s_due = now + IntervalSeconds; return; }
        if (System.Threading.Interlocked.CompareExchange(ref s_running, 1, 0) != 0) { return; }
        var worker = new System.Threading.Thread(() =>
        {
            try { Run(now); } catch { }
            System.Threading.Volatile.Write(ref s_running, 0);
        });
        worker.Start();
    }

    private static void Run(long now)
    {
        var root = CredentialContext.Enter(Credentials.Root);
        try
        {
            bool ran = Zenith.Core.Shell.Commands.PackageCommands.TryExclusive(() =>
            {
                if (!NativePackageCache.Automatic) { return; }
                var plan = NativePackageCache.Clean(PackageCacheMode.Aged, false, out int failed);
                if (plan.Remove.Count > 0)
                { Log.Write("pkg", "periodic cache cleanup removed " + (plan.Remove.Count - failed) + " files, " + PackageCachePolicy.Human(plan.Bytes)); }
            });
            if (ran) { s_due = now + IntervalSeconds; }
            root.Dispose();
        }
        catch (Exception error)
        {
            // The pinned runtime skips unwind-finally on some failures: restore credentials on both paths.
            root.Dispose();
            s_due = now + IntervalSeconds;
            Log.Write("pkg", "periodic cache cleanup skipped: " + error.Message);
        }
    }
}
