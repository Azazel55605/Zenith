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
using Zenith.Core.Security;
using Zenith.Core.Configuration;
using Zenith.Core.Time;

namespace Zenith.Core.Shell.Commands;

/// <summary>System information, storage and power commands.</summary>
internal static class SystemCommands
{
    private static readonly string[] s_days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] s_months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    public static void Register(Action<Command> add)
    {
        var register = add;
        add = command => register(new Command(command.Name, command.Usage, command.Summary, c =>
        {
            if (command.Name is "install" or "mkfs.ext2" or "fsck.ext2" or "umount" or "reboot" or "poweroff" or "crash"
                || (command.Name == "mount" && c.Args.Length > 0)
                || (command.Name == "localectl" && c.Args.Length > 0 && c.Args[0] == "set-keymap")
                || (command.Name == "timedatectl" && c.Args.Length > 0 && c.Args[0] == "set-timezone"))
            { GuardedFilesystem.RequireRoot(); }
            return command.Run(c);
        }));
        add(new Command("cpu", "cpu status | irq-test | test [iterations]", "Inspect experimental native CPU workers", Zenith.Core.Multiprocessing.CpuWorkers.Command));
        add(new Command("uname", "uname [-a]", "Print system information", Uname));
        add(new Command("hostname", "hostname", "Print the host name", c => { c.WriteLine(c.Shell.Get("HOSTNAME")); return 0; }));
        add(new Command("securityprobe", "securityprobe", "Check security primitives and hardware entropy", SecurityProbe));
        AccountCommands.Register(add);
        ConfigurationCommands.Register(add);
        PackageCommands.Register(add);
        NetworkCommands.Register(add);
        add(new Command("accessprobe", "accessprobe directory", "Verify native VFS authorization (root, ext2)", c => { if (c.Args.Length != 1) { return c.Fail("specify an ext2 directory"); } c.WriteLine(NativeAccessProbe.Run(c.Shell.ResolvePath(c.Args[0]))); return 0; }));
        add(new Command("date", "date [-u]", "Print the date and time (-u: in UTC)", Date));
        add(new Command("timedatectl", "timedatectl [status | list-timezones | set-timezone zone]", "Show or set the time zone", Timedatectl));
        add(new Command("uptime", "uptime", "Time since boot", Uptime));
        add(new Command("free", "free", "Show memory usage", Free));
        add(new Command("df", "df", "Show filesystem space usage", Df));
        add(new Command("mount", "mount [[-t fat|ext2] partition mountpoint]", "Show mounts, or mount a FAT/ext2 partition", Mount));
        add(new Command("mkfs.ext2", "mkfs.ext2 partition [--yes] [-L label]", "Format an unmounted secondary partition as ext2", MkfsExt2));
        add(new Command("fsprobe", "fsprobe directory", "Test file I/O and open-file deletion in a temporary directory", FileProbe));
        add(new Command("fsck.ext2", "fsck.ext2 partition", "Check an unmounted ext2 partition without repairs", FsckExt2));
        add(new Command("ln", "ln -s target link", "Create a symbolic link", Symlink));
        add(new Command("readlink", "readlink link", "Print a symbolic link target", ReadLink));
        add(new Command("unlink", "unlink file", "Remove a file or symbolic link", Unlink));
        add(new Command("lsblk", "lsblk", "List disks and partitions", Lsblk));
        add(new Command("loadkeys", "loadkeys [layout]", "Switch the keyboard layout for this session", LoadKeys));
        add(new Command("localectl", "localectl [status | list-keymaps | set-keymap layout]", "Show or set the saved keyboard layout", Localectl));
        add(new Command("dmesg", "dmesg", "Print the kernel log", Dmesg));
        add(new Command("logger", "logger [message...]", "Write a message (or stdin) to the kernel log", Logger));
        add(new Command("crash", "crash", "Trigger a kernel panic (for testing)", _ => { KernelPanic.Request("crash command"); return 0; }));
        add(new Command("boot", "boot status|confirm|rollback|recover", "Manage kernel boot generations", Zenith.Core.Boot.NativeKernelUpdates.Boot));
        add(new Command("install", "install [disk] [--yes | --setup]", "Install Zenith onto a disk", Install));
        add(new Command("sync", "sync", "Write all cached filesystem data to disk", c => { SystemMounts.SyncAll(); return 0; }));
        add(new Command("umount", "umount mountpoint", "Unmount a filesystem", Umount));
        add(new Command("reboot", "reboot", "Sync, unmount and restart the machine", _ => { PowerControl.Reboot(); return 0; }));
        add(new Command("poweroff", "poweroff", "Sync, unmount and turn the machine off", _ => { PowerControl.PowerOff(); return 0; }));
    }

    private static int SecurityProbe(CommandContext c)
    {
        byte[] first = new byte[16], second = new byte[16];
        if (!Zenith.Core.Security.HardwareEntropy.Source.TryFill(first)
            || !Zenith.Core.Security.HardwareEntropy.Source.TryFill(second))
        {
            return c.Fail("RDSEED unavailable; password creation disabled");
        }
        int different = 0;
        for (int i = 0; i < 16; i++) { different |= first[i] ^ second[i]; }
        if (different == 0 || !Zenith.Core.Security.PasswordHash.Verify("native test", "$zenith-pbkdf2-sha256$600000$AAECAwQFBgcICQoLDA0ODw==$3Ap115woogot/jebBfe+V/e+qHiHi5OWobQCImUZ+kE="))
        {
            return c.Fail("security primitive self-test failed");
        }
        var identity = new Zenith.Core.Security.Credentials(1000, 100);
        using (Zenith.Core.Security.CredentialContext.Enter(identity))
        {
            using (Zenith.Core.Security.CredentialContext.Enter(Zenith.Core.Security.Credentials.Root))
            {
                if (!Zenith.Core.Security.CredentialContext.Current.IsRoot) { return c.Fail("credential scope failed"); }
            }
            if (Zenith.Core.Security.CredentialContext.Current.Uid != 1000) { return c.Fail("credential restore failed"); }
        }
        uint parent = CredentialContext.Current.Uid;
        uint[] observed = new uint[3];
        var worker = new System.Threading.Thread(() =>
        {
            observed[0] = CredentialContext.Current.Uid;
            using (CredentialContext.Enter(new Credentials(2222,2222)))
            { System.Threading.Thread.Sleep(40); observed[1] = CredentialContext.Current.Uid; }
            System.Threading.Volatile.Write(ref observed[2], 1);
        });
        worker.Start();
        while (System.Threading.Volatile.Read(ref observed[2]) == 0) { System.Threading.Thread.Sleep(20); }
        if (observed[0] != 65534 || observed[1] != 2222 || CredentialContext.Current.Uid != parent)
        { return c.Fail("credential thread isolation failed"); }
        c.WriteLine("security primitives ok (RDSEED, PBKDF2-SHA256, credential scopes)");
        return 0;
    }

    private static int Symlink(CommandContext c)
    {
        if (c.Args.Length != 3 || c.Args[0] != "-s")
        {
            return c.Fail("usage: ln -s target link (hard links are not supported)");
        }

        string path = c.Resolve(c.Args[2]);
        if (!VfsManager.TryOpenDirectory(Path.GetDirectoryName(path) ?? "/", out var parent))
        {
            return c.Fail("cannot open link parent directory");
        }
        using (parent)
        {
            // Keep target verbatim: relative targets are relative to the link's parent.
            if (!parent.TrySymlink(Path.GetFileName(path), c.Args[1], out var link))
            {
                return c.Fail("cannot create link (existing name, unsupported filesystem or invalid target)");
            }
            link.Dispose();
        }
        return 0;
    }

    private static int ReadLink(CommandContext c)
    {
        if (c.Args.Length != 1)
        {
            return c.Fail("usage: readlink link");
        }
        string path = c.Resolve(c.Args[0]);
        if (!VfsManager.TryOpenDirectory(Path.GetDirectoryName(path) ?? "/", out var parent))
        {
            return c.Fail("cannot open link parent directory");
        }
        using (parent)
        {
            // Lookup the final entry without following it: dangling links work too.
            if (!parent.TryLookup(Path.GetFileName(path), out var link))
            {
                return c.Fail("link does not exist");
            }
            using (link)
            {
                if (!link.Inode.InodeOperations.TryReadLink(link.Inode, out string? target) || target is null)
                {
                    return c.Fail("not a readable symbolic link");
                }
                c.WriteLine(target);
            }
        }
        return 0;
    }

    private static int FileProbe(CommandContext c)
    {
        if (c.Args.Length != 1) { return c.Fail("usage: fsprobe directory"); }
        c.WriteLine(FileSystemProbe.Run(c.Resolve(c.Args[0])));
        return 0;
    }

    private static int FsckExt2(CommandContext c)
    {
        if (c.Args.Length != 1) { return c.Fail("usage: fsck.ext2 partition (check-only)"); }
        bool clean = SystemMounts.CheckExt2(c.Args[0], out string report);
        c.WriteLine(report);
        return clean ? 0 : 1;
    }

    private static int Unlink(CommandContext c)
    {
        if (c.Args.Length != 1)
        {
            return c.Fail("usage: unlink file");
        }
        // VFS removes the final directory entry; it does not follow a link target.
        return VfsManager.TryUnlink(c.Resolve(c.Args[0])) ? 0 : c.Fail("cannot unlink file");
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
                SystemConfiguration.Settings.SetTimeZone(c.Args[1]);
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
        if (c.Args.Length == 4 && c.Args[0] == "-t")
        {
            string? error = SystemMounts.Mount(c.Args[2], c.Resolve(c.Args[3]), c.Args[1]);
            return error is null ? 0 : c.Fail(error);
        }
        if (c.Args.Length == 2)
        {
            string? error = SystemMounts.Mount(c.Args[0], c.Resolve(c.Args[1]));
            return error is null ? 0 : c.Fail(error);
        }

        if (c.Args.Length != 0)
        {
            return c.Fail("usage: mount [[-t fat|ext2] partition mountpoint]");
        }

        foreach (VfsManager.VfsMount m in VfsManager.Mounts)
        {
            c.WriteLine(Source(m) + " on " + m.MountPoint + " type " + (m.Name == SystemMounts.Fat ? "vfat" : m.Name == "rootfs" || m.Name == "tmpfs" ? "tmpfs (" + m.Name + ")" : m.Name));
        }

        return 0;
    }

    private static int MkfsExt2(CommandContext c)
    {
        if (c.Args.Length == 0)
        {
            return c.Fail("usage: mkfs.ext2 partition [--yes] [-L label]");
        }
        bool confirmed = false, labeled = false;
        string label = "";
        for (int i = 1; i < c.Args.Length; i++)
        {
            if (c.Args[i] == "--yes" && !confirmed)
            {
                confirmed = true;
            }
            else if (c.Args[i] == "-L" && !labeled && i + 1 < c.Args.Length)
            {
                label = c.Args[++i];
                labeled = true;
            }
            else
            {
                return c.Fail("usage: mkfs.ext2 partition [--yes] [-L label]");
            }
        }
        string? error = SystemMounts.FormatExt2(c.Args[0], label, confirmed);
        if (error is not null)
        {
            return c.Fail(error);
        }
        c.WriteLine("Formatted " + c.Args[0] + " as ext2");
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
                SystemConfiguration.Settings.SetKeyboard(c.Args[1]);
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
            Log.Write("user", line);
        }

        return 0;
    }

    private static int Install(CommandContext c)
    {
        var candidates = Installer.Candidates();
        bool confirmed = Array.IndexOf(c.Args, "--yes") >= 0;
        bool setup = Array.IndexOf(c.Args, "--setup") >= 0;
        string? selected = null;
        foreach (string arg in c.Args)
        {
            if (arg != "--yes" && arg != "--setup")
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

        InstallationProfile? profile = null;
        if (setup)
        {
            GuardedFilesystem.RequireRoot();
            if (SystemMounts.Mode != BootMode.Live) { return c.Fail("use the ISO live rescue session for initial setup"); }
            if (!c.IsTerminal || c.Input is not null || c.Shell.ReadInputLine is null || c.Shell.ReadSecret is null)
            { return c.Fail("--setup requires an interactive terminal"); }
            string? hostname = c.Shell.ReadInputLine("Hostname: ");
            string? user = c.Shell.ReadInputLine("User name: ");
            if (hostname is null || user is null) { return 130; }
            InstallationProfile.ValidateNames(hostname, user);
            string? password = c.Shell.ReadSecret("User password: ");
            string? repeat = c.Shell.ReadSecret("Repeat user password: ");
            string? root = c.Shell.ReadSecret("Root password: ");
            string? rootRepeat = c.Shell.ReadSecret("Repeat root password: ");
            if (password is null || repeat is null || root is null || rootRepeat is null) { return 130; }
            c.WriteLine("Test in a VM first. Keep the live ISO for recovery.");
            string? erase = c.Shell.ReadInputLine("Type ERASE " + selected + " to confirm: ");
            if (erase != "ERASE " + selected) { return c.Fail("installation cancelled; target unchanged"); }
            profile = InstallationProfile.Create(hostname, user, root, rootRepeat, password, repeat, HardwareEntropy.Source);
            password = repeat = root = rootRepeat = null;
        }
        else if (!confirmed) { return c.Fail("this erases " + selected + "; add --yes or use --setup"); }

        // Progress goes straight to Out, skipping the cancellation check in WriteLine: stopping
        // between partitioning and copying would leave a half-written disk.
        Installer.Install(target, step => c.Out.Write(Ansi.Cyan("==> ") + step + "\n"), profile);
        c.WriteLine("Zenith installed and UEFI bootable. Power off, eject the ISO and boot the disk.");
        c.WriteLine("Signed kernel updates: pkg kernel install FILE; reboot and test, then boot confirm.");
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
