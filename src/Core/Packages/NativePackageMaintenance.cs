using System;
using System.Diagnostics;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Packages;

/// <summary>
/// The periodic package cache cleanup: two minutes after boot, then every six hours, archives older
/// than a day and stages older than an hour leave the cache (<see cref="PackageCachePolicy"/>). It
/// runs from the kernel loop only when the package service is idle (a busy service just waits for
/// the next turn), as root, on an installed ext2 root, and never when automatic cleanup is off.
/// A failure is logged; it never blocks the session.
/// </summary>
internal static class PackageMaintenance
{
    private const long FirstSeconds = 120, IntervalSeconds = 6 * 60 * 60, RetrySeconds = 10 * 60;
    private static long s_due = -1;

    public static void Tick()
    {
        long now = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        if (s_due < 0) { s_due = now + FirstSeconds; return; }
        if (now < s_due) { return; }
        s_due = now + RetrySeconds;
        if (SystemMounts.Mode != BootMode.Installed) { s_due = now + IntervalSeconds; return; }
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
