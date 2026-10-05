using System;
using System.Diagnostics;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Input;
using Zenith.Core.Storage;
using Zenith.Core.Time;

namespace Zenith.Core.Shell.Commands;

/// <summary>System information, storage and power commands.</summary>
internal static class SystemCommands
{
    private static readonly string[] s_days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] s_months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public static void Register(Action<Command> add)
    {
        add(new Command("uname", "uname [-a]", "Print system information", Uname));
        add(new Command("hostname", "hostname", "Print the host name", c => { c.WriteLine(c.Shell.Get("HOSTNAME")); return 0; }));
        add(new Command("whoami", "whoami", "Print the current user", c => { c.WriteLine(c.Shell.Get("USER")); return 0; }));
        add(new Command("id", "id", "Print user and group ids", c => { c.WriteLine("uid=1000(user) gid=100(users) groups=100(users)"); return 0; }));
        add(new Command("date", "date [-u]", "Print the date and time (-u: in UTC)", Date));
        add(new Command("timedatectl", "timedatectl [status | list-timezones | set-timezone zone]", "Show or set the time zone", Timedatectl));
        add(new Command("uptime", "uptime", "Time since boot", Uptime));
        add(new Command("free", "free", "Show memory usage", Free));
        add(new Command("df", "df", "Show filesystem space usage", Df));
        add(new Command("mount", "mount [partition mountpoint]", "Show the mount table, or mount a FAT partition", Mount));
        add(new Command("lsblk", "lsblk", "List disks and partitions", Lsblk));
        add(new Command("loadkeys", "loadkeys [layout]", "Switch the keyboard layout for this session", LoadKeys));
        add(new Command("localectl", "localectl [status | list-keymaps | set-keymap layout]", "Show or set the saved keyboard layout", Localectl));
        add(new Command("dmesg", "dmesg", "Print the kernel log", Dmesg));
        add(new Command("logger", "logger [message...]", "Write a message (or stdin) to the kernel log", Logger));
        add(new Command("crash", "crash", "Trigger a kernel panic (for testing)", _ => { KernelPanic.Request("crash command"); return 0; }));
        add(new Command("install", "install [disk] [--yes]", "Install Zenith onto a disk", Install));
        add(new Command("sync", "sync", "Write all cached filesystem data to disk", c => { SystemMounts.SyncAll(); return 0; }));
        add(new Command("umount", "umount mountpoint", "Unmount a filesystem", Umount));
        add(new Command("reboot", "reboot", "Sync, unmount and restart the machine", _ => { PowerControl.Reboot(); return 0; }));
        add(new Command("poweroff", "poweroff", "Sync, unmount and turn the machine off", _ => { PowerControl.PowerOff(); return 0; }));
    }

    private static int Uname(CommandContext c)
    {
        bool all = c.Args.Length > 0 && c.Args[0] == "-a";
        c.WriteLine(all ? "Zenith " + c.Shell.Get("HOSTNAME") + " 0.1 Cosmos-Gen3 x86_64" : "Zenith");
        return 0;
    }

    private static int Date(CommandContext c)
    {
        bool utc = c.Args.Length > 0 && c.Args[0] == "-u";
        DateTime now = SystemClock.UtcNow;
        c.WriteLine(utc ? SystemClock.Format(now, "UTC") : SystemClock.Format(SystemClock.ToLocal(now), SystemClock.Abbreviation));
        return 0;
    }

    private static int Timedatectl(CommandContext c)
    {
        string verb = c.Args.Length > 0 ? c.Args[0] : "status";
        switch (verb)
        {
            case "status":
                DateTime utc = SystemClock.UtcNow;
                TimeSpan offset = SystemClock.Zone.OffsetAt(utc);
                c.WriteLine("      Local time: " + SystemClock.Format(SystemClock.ToLocal(utc), SystemClock.Abbreviation));
                c.WriteLine("  Universal time: " + SystemClock.Format(utc, "UTC"));
                c.WriteLine("       Time zone: " + SystemClock.Zone.Name + " (" + SystemClock.Abbreviation + ", "
                    + (offset < TimeSpan.Zero ? "-" : "+") + SystemClock.Pad2(Math.Abs(offset.Hours)) + SystemClock.Pad2(Math.Abs(offset.Minutes)) + ")");
                return 0;
            case "list-timezones":
                foreach (TimeZoneInfoLite zone in TimeZones.All)
                {
                    c.WriteLine(zone.Name);
                }

                c.WriteLine(Ansi.Dim("Fixed offsets work too: UTC+2, UTC-5:30"));
                return 0;
            case "set-timezone" when c.Args.Length == 2:
                if (!SystemClock.TrySetZone(c.Args[1]))
                {
                    return c.Fail("unknown time zone '" + c.Args[1] + "' (see 'timedatectl list-timezones')");
                }

                SystemClock.Save();
                return 0;
            default:
                return c.Fail("usage: timedatectl [status | list-timezones | set-timezone zone]");
        }
    }

    private static int Uptime(CommandContext c)
    {
        long s = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        c.WriteLine("up " + (s / 86400 > 0 ? (s / 86400) + " days, " : "") + Pad2((int)(s / 3600 % 24)) + ":" + Pad2((int)(s / 60 % 60)) + ":" + Pad2((int)(s % 60)));
        return 0;
    }

    private static int Free(CommandContext c)
    {
        const ulong PageSize = 4096;
        ulong total = MemoryInfo.TotalPages * PageSize / 1024;
        ulong free = MemoryInfo.FreePages * PageSize / 1024;
        c.WriteLine("            total        used        free");
        c.WriteLine("Mem: " + (total + "K").PadLeft(12) + ((total - free) + "K").PadLeft(12) + (free + "K").PadLeft(12));
        return 0;
    }

    private static int Df(CommandContext c)
    {
        c.WriteLine("Filesystem      Size      Used     Avail  Use%  Mounted on");
        foreach (VfsManager.VfsMount m in VfsManager.Mounts)
        {
            if (!VfsManager.TryStatFs(m.MountPoint, out VfsStatFs st))
            {
                continue;
            }

            ulong size = st.Blocks * st.BlockSize, avail = st.Bavail * st.BlockSize, used = size - st.Bfree * st.BlockSize;
            int pct = size == 0 ? 0 : (int)(used * 100 / size);
            c.WriteLine(Source(m).PadRight(12) + Human(size).PadLeft(8) + Human(used).PadLeft(10) + Human(avail).PadLeft(10)
                + (pct + "%").PadLeft(6) + "  " + m.MountPoint);
        }

        return 0;
    }

    private static int Mount(CommandContext c)
    {
        if (c.Args.Length == 2)
        {
            string? error = SystemMounts.Mount(c.Args[0], c.Resolve(c.Args[1]));
            return error is null ? 0 : c.Fail(error);
        }

        if (c.Args.Length != 0)
        {
            return c.Fail("usage: mount [partition mountpoint]");
        }

        foreach (VfsManager.VfsMount m in VfsManager.Mounts)
        {
            c.WriteLine(Source(m) + " on " + m.MountPoint + " type " + (m.Name == SystemMounts.Fat ? "vfat" : "tmpfs (" + m.Name + ")"));
        }

        return 0;
    }

    private static int Umount(CommandContext c)
    {
        if (c.Args.Length != 1)
        {
            return c.Fail("usage: umount mountpoint");
        }

        string? error = SystemMounts.Unmount(c.Resolve(c.Args[0]));
        return error is null ? 0 : c.Fail(error);
    }

    private static string Source(VfsManager.VfsMount m) => m.Partition?.Name ?? m.Name;

    private static int Lsblk(CommandContext c)
    {
        c.WriteLine("NAME            SIZE  TYPE  MOUNTPOINT");
        foreach (IBlockDevice disk in StorageManager.Devices)
        {
            c.WriteLine(disk.Name.PadRight(12) + Human(disk.BlockCount * disk.BlockSize).PadLeft(8) + "  disk");
            foreach (Partition p in StorageManager.GetPartitions(disk))
            {
                string mountPoint = "";
                foreach (VfsManager.VfsMount m in VfsManager.Mounts)
                {
                    if (ReferenceEquals(m.Partition, p))
                    {
                        mountPoint = m.MountPoint;
                    }
                }

                c.WriteLine(("└─" + p.Name).PadRight(12) + Human(p.BlockCount * p.BlockSize).PadLeft(8) + "  part  " + mountPoint);
            }
        }

        if (StorageManager.DeviceCount == 0)
        {
            c.WriteLine(Ansi.Dim("(no disks; attach one with -drive in QEMU)"));
        }

        return 0;
    }

    private static int Dmesg(CommandContext c)
    {
        foreach (string line in Log.Lines)
        {
            c.WriteLine(line);
        }

        return 0;
    }

    private static int LoadKeys(CommandContext c)
    {
        if (c.Args.Length == 0)
        {
            return ListKeymaps(c);
        }

        return KeyboardLayouts.Apply(c.Args[0]) ? 0 : c.Fail("unknown layout '" + c.Args[0] + "' (see 'localectl list-keymaps')");
    }

    private static int Localectl(CommandContext c)
    {
        string verb = c.Args.Length > 0 ? c.Args[0] : "status";
        switch (verb)
        {
            case "status":
                c.WriteLine("   Keymap: " + KeyboardLayouts.Current);
                return 0;
            case "list-keymaps":
                return ListKeymaps(c);
            case "set-keymap" when c.Args.Length == 2:
                if (!KeyboardLayouts.Apply(c.Args[1]))
                {
                    return c.Fail("unknown layout '" + c.Args[1] + "'");
                }

                KeyboardLayouts.Save(KeyboardLayouts.Current);
                return 0;
            default:
                return c.Fail("usage: localectl [status | list-keymaps | set-keymap layout]");
        }
    }

    private static int ListKeymaps(CommandContext c)
    {
        foreach (var (name, description) in KeyboardLayouts.All)
        {
            c.WriteLine((name == KeyboardLayouts.Current ? "* " : "  ") + name.PadRight(8) + description);
        }

        return 0;
    }

    private static int Logger(CommandContext c)
    {
        string text = c.Args.Length > 0 ? string.Join(" ", c.Args) : c.Stdin ?? string.Empty;
        foreach (string line in CommandContext.Lines(text))
        {
            Log.Write(c.Shell.Get("USER"), line);
        }

        return 0;
    }

    private static int Install(CommandContext c)
    {
        var candidates = Installer.Candidates();
        bool confirmed = Array.IndexOf(c.Args, "--yes") >= 0;
        string? selected = null;
        foreach (string arg in c.Args)
        {
            if (arg != "--yes")
            {
                selected = arg;
            }
        }

        if (selected is null)
        {
            c.WriteLine(Ansi.Bold("Zenith installer") + "  (running from: " + (SystemMounts.Mode == BootMode.Live ? "live RAM disk" : "installed disk") + ")");
            if (candidates.Count == 0)
            {
                c.WriteLine("No suitable disk found (needs " + Human(Installer.MinimumBytes) + " or more, other than the system disk).");
                return 1;
            }

            c.WriteLine("Target disks:");
            foreach (IBlockDevice disk in candidates)
            {
                c.WriteLine("  " + disk.Name.PadRight(10) + Human(disk.BlockCount * disk.BlockSize).PadLeft(8));
            }

            c.WriteLine("Run 'install <disk> --yes' to erase that disk and install.");
            return 0;
        }

        IBlockDevice? target = candidates.Find(d => d.Name == selected);
        if (target is null)
        {
            return c.Fail(selected + ": not a suitable disk (see 'install')");
        }

        if (!confirmed)
        {
            return c.Fail("this erases " + selected + "; add --yes to confirm");
        }

        // Progress goes straight to Out, skipping the cancellation check in WriteLine: stopping
        // between partitioning and copying would leave a half-written disk.
        Installer.Install(target, step => c.Out.Write(Ansi.Cyan("==> ") + step + "\n"));
        c.WriteLine("Root filesystem installed. To make the disk bootable, run on the host:");
        c.WriteLine("  tools/make-bootable.sh <disk image>");
        return 0;
    }

    public static string Human(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
        {
            ulong tenths = bytes / (1024UL * 1024 * 1024 / 10);
            return (tenths / 10) + "." + (tenths % 10) + "G";
        }

        if (bytes >= 1024 * 1024)
        {
            return (bytes / (1024 * 1024)) + "M";
        }

        return (bytes / 1024) + "K";
    }

    private static string Pad2(int v) => v < 10 ? "0" + v : v.ToString();
}
