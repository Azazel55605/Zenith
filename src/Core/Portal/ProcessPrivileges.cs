using System;
using System.Collections.Generic;

namespace Zenith.Core.Portal;

/// <summary>Administrative areas a program can be granted through the Authorization portal.</summary>
[Flags]
internal enum Privilege : uint
{
    None = 0,
    Disks = 1,            // zenith-native-disk-abi: partition, format, mount (calls 647/648)
    SystemSettings = 2,   // settings for all users: keyboard, time zone, session, display, system appearance
}

/// <summary>What each running program was granted, until it exits. A grant makes the kernel serve
/// that area's native calls as root for that process; every other call keeps the program's own
/// identity. Grants never pass to other processes, and a new process with a reused pid starts with none.</summary>
internal static class ProcessPrivileges
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<int, Privilege> s_grants = new();

    public static void Grant(int pid, Privilege privilege) { lock (s_gate) { s_grants[pid] = Held(pid) | privilege; } }
    public static void Revoke(int pid, Privilege privilege)
    {
        lock (s_gate)
        {
            var left = Held(pid) & ~privilege;
            if (left == Privilege.None) { s_grants.Remove(pid); } else { s_grants[pid] = left; }
        }
    }
    public static void Drop(int pid) { lock (s_gate) { s_grants.Remove(pid); } }
    public static Privilege Held(int pid) { lock (s_gate) { return s_grants.TryGetValue(pid, out var p) ? p : Privilege.None; } }
    public static bool Holds(int pid, Privilege privilege) => privilege != Privilege.None && (Held(pid) & privilege) == privilege;

    /// <summary>The privilege that would make this native call run as root, or None.</summary>
    public static Privilege For(ulong call, ulong arg0) => call switch
    {
        647 or 648 => Privilege.Disks,
        // System settings (620/621, 625/626), display (640/641), system appearance and materials.
        620 or 621 or 625 or 626 or 640 or 641 or 608 or 609 or 616 or 617 or 618 or 619 => Privilege.SystemSettings,
        // Scoped appearance: only the system scope (0); a personal scope stays the caller's own.
        622 when arg0 == 0 => Privilege.SystemSettings,
        _ => Privilege.None,
    };

    /// <summary>Whether this process's call runs as root because of a grant.</summary>
    public static bool Elevates(int pid, ulong call, ulong arg0)
    {
        var needed = For(call, arg0);
        return needed != Privilege.None && Holds(pid, needed);
    }

    internal static void Reset() { lock (s_gate) { s_grants.Clear(); } }
}
