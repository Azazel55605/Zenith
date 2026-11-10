using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Security;
using Zenith.Core.Storage;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Core.Shell.Commands;

/// <summary>
/// <c>disk</c>: list disks, partitions, filesystems and free space; create, delete and grow GPT
/// partitions. Edits need root and <c>--yes</c> (without it the change is only described), and touch
/// only disks with nothing mounted, so the running system's disk is changed from the rescue system;
/// the one exception is creating a partition in free space there, which appears after a restart.
/// Growing extends the partition and then its ext2 volume, after a read-only plan of both.
/// </summary>
internal static class DiskCommands
{
    private static readonly Guid LinuxFilesystem = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private const string Usage = "disk [list] | init DISK [--yes] | create DISK SIZE|rest [ext2|none] [-L LABEL] [--yes] | delete PARTITION [--yes] | grow PARTITION [SIZE|max] [--yes]";
    private static readonly object s_gate = new();

    public static void Register(Action<Command> add)
        => add(new Command("disk", Usage, "Disks, partitions and free space; create, delete and grow partitions", Run));

    private static int Run(CommandContext c)
    {
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "list")) { return List(c); }
        GuardedFilesystem.RequireRoot();
        lock (s_gate)
        {
            return c.Args[0] switch
            {
                "init" => Init(c),
                "create" => Create(c),
                "delete" => Delete(c),
                "grow" => Grow(c),
                _ => c.Fail("usage: " + Usage),
            };
        }
    }

    private static List<DiskPartition> Table(IBlockDevice disk)
    {
        var result = new List<DiskPartition>();
        var entries = Gpt.Parse(disk);
        for (int i = 0; i < entries.Count; i++)
        {
            result.Add(new(i, entries[i].StartSector, entries[i].StartSector + entries[i].SectorCount - 1, entries[i].PartitionType));
        }
        return result;
    }

    private static string? MountOf(Partition partition)
    {
        foreach (var mount in VfsManager.Mounts) { if (ReferenceEquals(mount.Partition, partition)) { return mount.MountPoint; } }
        return null;
    }

    private static bool AnythingMounted(IBlockDevice disk)
    {
        foreach (var mount in VfsManager.Mounts) { if (mount.Partition is Partition p && ReferenceEquals(p.Host, disk)) { return true; } }
        return false;
    }

    private static bool IsSystemDisk(IBlockDevice disk) => ReferenceEquals(SystemMounts.RootPartition?.Host, disk);

    /// <summary>What a partition holds: ext2 (block size, label), FAT, or unknown. Reads its first sectors only.</summary>
    private static string Filesystem(IBlockDevice partition)
    {
        try
        {
            if (partition.BlockCount * partition.BlockSize >= 4096)
            {
                byte[] sb = Ext2VolumePolicy.ReadPrimary(partition);
                if (BitConverter.ToUInt16(sb, 56) == 0xEF53)
                {
                    string label = System.Text.Encoding.ASCII.GetString(sb, 120, 16).TrimEnd('\0');
                    return "ext2 " + (1024 << (int)BitConverter.ToUInt32(sb, 24)) / 1024 + "K" + (label.Length > 0 ? " \"" + label + "\"" : "");
                }
            }
            byte[] boot = new byte[partition.BlockSize];
            partition.ReadBlock(0, 1, boot);
            if (boot[510] == 0x55 && boot[511] == 0xAA && System.Text.Encoding.ASCII.GetString(boot, 82, 5) == "FAT32") { return "fat32"; }
        }
        catch { }
        return "-";
    }

    private static int List(CommandContext c)
    {
        foreach (IBlockDevice disk in StorageManager.Devices)
        {
            bool gpt = Gpt.IsGpt(disk);
            c.WriteLine(disk.Name + "  " + DiskLayout.Human(disk.BlockCount * disk.BlockSize) + "  " + (gpt ? "GPT" : "no GPT (read-only here)")
                + (IsSystemDisk(disk) ? "  system disk" : ""));
            var parts = StorageManager.GetPartitions(disk);
            var table = gpt ? Table(disk) : new List<DiskPartition>();
            foreach (var p in parts)
            {
                string type = "-";
                foreach (var entry in table)
                {
                    if (entry.Start == p.StartSector)
                    {
                        type = entry.Type == EfiSystem ? "EFI" : entry.Type == LinuxFilesystem ? "Linux" : "other";
                    }
                }
                string role = ReferenceEquals(p, SystemMounts.RootPartition) ? " (root)" : ReferenceEquals(p, SystemMounts.BootPartition) ? " (boot)" : "";
                c.WriteLine("  " + p.Name.PadRight(10) + DiskLayout.Human(p.BlockCount * p.BlockSize).PadLeft(10) + "  start " + p.StartSector.ToString().PadRight(10)
                    + type.PadRight(6) + Filesystem(p).PadRight(18) + (MountOf(p) ?? "") + role);
            }
            if (gpt && disk.BlockSize == 512)
            {
                foreach (var region in DiskLayout.Free(disk.BlockCount, table))
                {
                    c.WriteLine("  " + "free".PadRight(10) + DiskLayout.Human(region.Sectors * 512).PadLeft(10) + "  start " + region.Start);
                }
            }
        }
        if (StorageManager.DeviceCount == 0) { c.WriteLine("(no disks)"); }
        return 0;
    }

    private static IBlockDevice? Disk(string name)
    {
        foreach (IBlockDevice disk in StorageManager.Devices) { if (disk.Name == name) { return disk; } }
        return null;
    }

    private static Partition? Find(string name)
    {
        foreach (Partition p in StorageManager.Partitions) { if (p.Name == name) { return p; } }
        return null;
    }

    private static string? Editable(IBlockDevice disk)
    {
        if (disk.BlockSize != 512) { return disk.Name + ": only 512-byte sector disks are edited"; }
        if (!Gpt.IsGpt(disk)) { return disk.Name + ": no GPT; Zenith edits GPT disks only"; }
        return null;
    }

    /// <summary>A new, empty GPT (protective MBR, primary and backup tables). Erases the old table.</summary>
    private static int Init(CommandContext c)
    {
        if (c.Args.Length is < 2 or > 3 || (c.Args.Length == 3 && c.Args[2] != "--yes")) { return c.Fail("usage: disk init DISK [--yes]"); }
        IBlockDevice? disk = Disk(c.Args[1]);
        if (disk is null) { return c.Fail(c.Args[1] + ": no such disk (see disk list)"); }
        if (disk.BlockSize != 512) { return c.Fail(disk.Name + ": only 512-byte sector disks are edited"); }
        if (AnythingMounted(disk) || IsSystemDisk(disk)) { return c.Fail(disk.Name + " is in use; unmount its partitions first"); }
        if (disk.BlockCount < 2 * DiskLayout.Alignment + DiskLayout.Reserved) { return c.Fail(disk.Name + " is too small for a partition table"); }
        int existing = StorageManager.GetPartitions(disk).Count;
        string summary = "a new empty GPT on " + disk.Name + " (" + DiskLayout.Human(disk.BlockCount * 512) + ")"
            + (existing > 0 ? ", removing its " + existing + " partitions" : "");
        if (c.Args.Length != 3) { c.WriteLine("would write " + summary); return c.Fail("pass --yes to write it"); }
        Gpt.Create(disk);
        GptChecksums.Complete(disk);
        StorageManager.RescanPartitions(disk);
        Log.Write("disk", "wrote " + summary);
        c.WriteLine("wrote " + summary);
        return 0;
    }

    private static int Create(CommandContext c)
    {
        // create DISK SIZE|rest [ext2|none] [-L LABEL] [--yes]
        if (c.Args.Length < 3) { return c.Fail("usage: disk create DISK SIZE|rest [ext2|none] [-L LABEL] [--yes]"); }
        bool yes = false; string fs = "ext2", label = "";
        for (int i = 3; i < c.Args.Length; i++)
        {
            if (c.Args[i] == "--yes") { yes = true; }
            else if (c.Args[i] is "ext2" or "none") { fs = c.Args[i]; }
            else if (c.Args[i] == "-L" && i + 1 < c.Args.Length) { label = c.Args[++i]; }
            else { return c.Fail("unexpected argument: " + c.Args[i]); }
        }
        IBlockDevice? disk = Disk(c.Args[1]);
        if (disk is null) { return c.Fail(c.Args[1] + ": no such disk (see disk list)"); }
        if (Editable(disk) is string refused) { return c.Fail(refused); }
        ulong? wanted = c.Args[2] == "rest" ? null : DiskLayout.ParseSize(c.Args[2]);
        if (c.Args[2] != "rest" && wanted is null) { return c.Fail("size must be whole MiB, at least 1M (e.g. 512M, 20G) or rest"); }
        var table = Table(disk);
        if (DiskLayout.Place(disk.BlockCount, table, wanted) is not DiskRegion region) { return c.Fail("no free region of that size on " + disk.Name); }
        bool live = AnythingMounted(disk);
        var temporary = new Partition(disk, region.Start, region.Sectors, disk.Name + "-new");
        if (fs == "ext2" && Ext2Formatter.Check(temporary, label) is string invalid) { return c.Fail(invalid); }
        string summary = "partition of " + DiskLayout.Human(region.Sectors * 512) + " at sector " + region.Start + " on " + disk.Name
            + (fs == "ext2" ? ", formatted ext2" + (label.Length > 0 ? " \"" + label + "\"" : "") : ", unformatted");
        if (!yes) { c.WriteLine("would create a " + summary); return c.Fail("pass --yes to create it"); }
        if (!Gpt.AddPartition(disk, region.Start, region.Sectors, LinuxFilesystem)) { return c.Fail("the partition table refused the new entry (no free slot?)"); }
        GptChecksums.Complete(disk);
        Partition target = temporary;
        if (!live)
        {
            StorageManager.RescanPartitions(disk);
            foreach (var p in StorageManager.GetPartitions(disk)) { if (p.StartSector == region.Start) { target = p; } }
        }
        if (fs == "ext2") { Ext2Formatter.Format(target, label); }
        Log.Write("disk", "created " + summary);
        c.WriteLine("created " + (live ? "a " : target.Name + ": ") + summary
            + (live ? "; the system disk is in use, so the partition appears after a restart" : ""));
        return 0;
    }

    private static int Delete(CommandContext c)
    {
        if (c.Args.Length is < 2 or > 3 || (c.Args.Length == 3 && c.Args[2] != "--yes")) { return c.Fail("usage: disk delete PARTITION [--yes]"); }
        Partition? target = Find(c.Args[1]);
        if (target is null) { return c.Fail(c.Args[1] + ": no such partition (see disk list)"); }
        IBlockDevice disk = target.Host;
        if (Editable(disk) is string refused) { return c.Fail(refused); }
        if (AnythingMounted(disk)) { return c.Fail(disk.Name + " has mounted partitions; unmount them (or use the rescue system for the system disk)"); }
        int index = Table(disk).FindIndex(p => p.Start == target.StartSector);
        if (index < 0) { return c.Fail("partition not found in the table"); }
        string summary = target.Name + " (" + DiskLayout.Human(target.BlockCount * target.BlockSize) + ", " + Filesystem(target) + ")";
        if (c.Args.Length != 3) { c.WriteLine("would delete " + summary + "; its data becomes unreachable"); return c.Fail("pass --yes to delete it"); }
        if (!Gpt.RemovePartition(disk, index)) { return c.Fail("the partition table refused the change"); }
        GptChecksums.Complete(disk);
        StorageManager.RescanPartitions(disk);
        Log.Write("disk", "deleted " + summary);
        c.WriteLine("deleted " + summary);
        return 0;
    }

    private static int Grow(CommandContext c)
    {
        // grow PARTITION [SIZE|max] [--yes]
        if (c.Args.Length is < 2 or > 4) { return c.Fail("usage: disk grow PARTITION [SIZE|max] [--yes]"); }
        bool yes = c.Args[^1] == "--yes";
        string size = c.Args.Length >= 3 && c.Args[2] != "--yes" ? c.Args[2] : "max";
        Partition? target = Find(c.Args[1]);
        if (target is null) { return c.Fail(c.Args[1] + ": no such partition (see disk list)"); }
        IBlockDevice disk = target.Host;
        if (Editable(disk) is string refused) { return c.Fail(refused); }
        if (AnythingMounted(disk))
        {
            return c.Fail(disk.Name + " has mounted partitions; unmount them"
                + (IsSystemDisk(disk) ? " (the system disk is grown from the rescue system: choose it in the boot menu)" : ""));
        }
        var table = Table(disk);
        var entry = table.Find(p => p.Start == target.StartSector);
        if (entry.Sectors == 0) { return c.Fail("partition not found in the table"); }
        ulong limit = DiskLayout.GrowLimit(disk.BlockCount, table, entry);
        ulong sectors = size == "max" ? limit : DiskLayout.ParseSize(size) ?? 0;
        if (sectors == 0) { return c.Fail("size must be whole MiB (e.g. 40G) or max"); }
        if (sectors <= entry.Sectors) { return c.Fail(target.Name + " is already " + DiskLayout.Human(entry.Sectors * 512) + "; shrinking is not supported"); }
        if (sectors > limit) { return c.Fail(target.Name + " can grow to at most " + DiskLayout.Human(limit * 512) + " (the next partition or the disk end)"); }
        var grown = new Partition(disk, entry.Start, sectors, target.Name);
        string? fsPlan = Ext2Resizer.Plan(grown, out var before, out var after);
        if (fsPlan is not null) { return c.Fail("cannot grow the ext2 volume on " + target.Name + ": " + fsPlan); }
        string summary = target.Name + " from " + DiskLayout.Human(entry.Sectors * 512) + " to " + DiskLayout.Human(sectors * 512)
            + " (ext2 " + before.Groups + " -> " + after.Groups + " groups)";
        if (!yes) { c.WriteLine("would grow " + summary); return c.Fail("pass --yes to grow it"); }
        int index = table.FindIndex(p => p.Start == entry.Start);
        if (!Gpt.ResizePartition(disk, index, sectors)) { return c.Fail("the partition table refused the change"); }
        GptChecksums.Complete(disk);
        StorageManager.RescanPartitions(disk);
        Partition? resized = null;
        foreach (var p in StorageManager.GetPartitions(disk)) { if (p.StartSector == entry.Start) { resized = p; } }
        if (resized is null) { return c.Fail("partition missing after the table change; the volume was not grown"); }
        Ext2Resizer.Grow(resized);
        bool clean = Ext2Checker.Check(resized, out string report);
        Log.Write("disk", "grew " + summary + "; check: " + report);
        c.WriteLine("grew " + summary + "; " + (clean ? "check clean" : "CHECK FAILED: " + report));
        return clean ? 0 : 1;
    }
}
