using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Security;
using Zenith.Core.Storage.Ext2;

namespace Zenith.Core.Storage;

internal enum DiskFilesystem { Unknown = 0, Ext2 = 1, Fat32 = 2 }
internal enum DiskPartitionType { Other = 0, Efi = 1, Linux = 2 }
internal enum DiskRole { None = 0, Root = 1, Boot = 2 }

/// <summary>A filesystem's own record of its state: clean, needing a check, or mounted (in use).</summary>
internal enum DiskFsState { Unknown = 0, Clean = 1, NeedsCheck = 2, InUse = 3 }

internal sealed record DiskPartInfo(Partition Partition, string Name, ulong Start, ulong Sectors, DiskPartitionType Type,
    DiskFilesystem Filesystem, uint BlockSize, string Label, string Mount, DiskRole Role, ulong GrowLimit,
    ulong UsedBytes = 0, DiskFsState State = DiskFsState.Unknown);

internal sealed record DiskInfo(IBlockDevice Device, string Name, ulong Sectors, bool Gpt, bool System, bool Mounted,
    bool Editable, List<DiskPartInfo> Parts, List<DiskRegion> Free, bool GptCopiesMatch = false);

/// <summary>
/// The disk operations behind <c>disk</c> and the Equinox ABI (zenith-native-disk-abi): one set of
/// rules for both. Partitions are named by disk and start sector, which deleting another partition
/// does not change. Edits touch GPT disks of 512-byte sectors with nothing mounted; creating a
/// partition in free space of the running system disk is the one exception (it appears after a
/// restart). Every edit holds <see cref="Gate"/>; the revision changes after each one, so a client
/// planning on an older view is refused. Root only; callers check credentials.
/// </summary>
internal static class DiskService
{
    public static readonly Guid LinuxFilesystem = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly object Gate = new();
    private static ulong s_revision = 1;
    public static ulong Revision => Interlocked.Read(ref s_revision);

    // ---------- Reading.

    public static List<DiskInfo> Describe()
    {
        var disks = new List<DiskInfo>();
        foreach (IBlockDevice disk in StorageManager.Devices)
        {
            bool gpt = disk.BlockSize == 512 && Gpt.IsGpt(disk);
            var table = gpt ? Table(disk) : new List<DiskPartition>();
            var parts = new List<DiskPartInfo>();
            foreach (var p in StorageManager.GetPartitions(disk))
            {
                var type = DiskPartitionType.Other;
                ulong growLimit = p.BlockCount;
                foreach (var entry in table)
                {
                    if (entry.Start != p.StartSector) { continue; }
                    type = entry.Type == EfiSystem ? DiskPartitionType.Efi : entry.Type == LinuxFilesystem ? DiskPartitionType.Linux : DiskPartitionType.Other;
                    growLimit = DiskLayout.GrowLimit(disk.BlockCount, table, entry);
                }
                var (fs, blockSize, label) = Probe(p);
                var role = ReferenceEquals(p, SystemMounts.RootPartition) ? DiskRole.Root : ReferenceEquals(p, SystemMounts.BootPartition) ? DiskRole.Boot : DiskRole.None;
                string? mount = MountOf(p);
                var (used, state) = Usage(p, fs, mount);
                parts.Add(new(p, p.Name, p.StartSector, p.BlockCount, type, fs, blockSize, label, mount ?? "", role, growLimit, used, state));
            }
            disks.Add(new(disk, disk.Name, disk.BlockCount, gpt, IsSystemDisk(disk), AnythingMounted(disk), gpt,
                parts, gpt ? DiskLayout.Free(disk.BlockCount, table) : new List<DiskRegion>(), gpt && GptChecksums.CopiesMatch(disk)));
        }
        return disks;
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

    /// <summary>Bytes in use and the filesystem's state: from the mounted filesystem, or for an
    /// unmounted ext2 volume from its superblock (block counts, and the clean flag a check or a clean
    /// unmount sets). Zero/unknown when neither applies.</summary>
    private static (ulong Used, DiskFsState State) Usage(IBlockDevice partition, DiskFilesystem fs, string? mount)
    {
        try
        {
            if (mount is not null)
            {
                if (VfsManager.TryStatFs(mount, out var stat) && stat.Blocks >= stat.Bfree) { return ((stat.Blocks - stat.Bfree) * stat.BlockSize, DiskFsState.InUse); }
                return (0, DiskFsState.InUse);
            }
            if (fs != DiskFilesystem.Ext2) { return (0, DiskFsState.Unknown); }
            byte[] sb = Ext2VolumePolicy.ReadPrimary(partition);
            uint log = BitConverter.ToUInt32(sb, 24), blocks = BitConverter.ToUInt32(sb, 4), free = BitConverter.ToUInt32(sb, 12);
            ushort state = BitConverter.ToUInt16(sb, 58);
            ulong used = log <= 6 && free <= blocks ? (ulong)(blocks - free) * (1024UL << (int)log) : 0;
            return (used, state == 1 ? DiskFsState.Clean : DiskFsState.NeedsCheck);
        }
        catch { return (0, DiskFsState.Unknown); }
    }

    /// <summary>What a partition holds, from its first sectors only.</summary>
    public static (DiskFilesystem Filesystem, uint BlockSize, string Label) Probe(IBlockDevice partition)
    {
        try
        {
            if (partition.BlockCount * partition.BlockSize >= 4096)
            {
                byte[] sb = Ext2VolumePolicy.ReadPrimary(partition);
                if (BitConverter.ToUInt16(sb, 56) == 0xEF53)
                {
                    uint log = BitConverter.ToUInt32(sb, 24);
                    return (DiskFilesystem.Ext2, log <= 6 ? 1024u << (int)log : 0, Encoding.ASCII.GetString(sb, 120, 16).TrimEnd('\0'));
                }
            }
            byte[] boot = new byte[partition.BlockSize];
            partition.ReadBlock(0, 1, boot);
            if (boot[510] == 0x55 && boot[511] == 0xAA && Encoding.ASCII.GetString(boot, 82, 5) == "FAT32")
            {
                return (DiskFilesystem.Fat32, 0, Encoding.ASCII.GetString(boot, 71, 11).TrimEnd(' ', '\0'));
            }
        }
        catch { }
        return (DiskFilesystem.Unknown, 0, "");
    }

    private static IBlockDevice? Disk(string name)
    {
        foreach (IBlockDevice disk in StorageManager.Devices) { if (disk.Name == name) { return disk; } }
        return null;
    }

    private static string? Editable(IBlockDevice disk)
    {
        if (disk.BlockSize != 512) { return disk.Name + ": only 512-byte sector disks are edited"; }
        if (!Gpt.IsGpt(disk)) { return disk.Name + ": no GPT; Zenith edits GPT disks only"; }
        return null;
    }

    /// <summary>A partition by name (CLI) or by disk and start sector (ABI).</summary>
    public static Partition? Find(string name)
    {
        foreach (Partition p in StorageManager.Partitions) { if (p.Name == name) { return p; } }
        return null;
    }

    public static Partition? Find(string disk, ulong start)
    {
        foreach (Partition p in StorageManager.Partitions) { if (p.Host.Name == disk && p.StartSector == start) { return p; } }
        return null;
    }

    private static void Changed() => Interlocked.Increment(ref s_revision);

    // ---------- Edits: each returns null on success or the reason it was refused. With apply false
    // nothing is written and summary describes what would happen.

    public static string? Init(string diskName, bool apply, out string summary)
    {
        summary = "";
        IBlockDevice? disk = Disk(diskName);
        if (disk is null) { return diskName + ": no such disk"; }
        if (disk.BlockSize != 512) { return disk.Name + ": only 512-byte sector disks are edited"; }
        if (AnythingMounted(disk) || IsSystemDisk(disk)) { return disk.Name + " is in use; unmount its partitions first"; }
        if (disk.BlockCount < 2 * DiskLayout.Alignment + DiskLayout.Reserved) { return disk.Name + " is too small for a partition table"; }
        int existing = StorageManager.GetPartitions(disk).Count;
        summary = "a new empty GPT on " + disk.Name + " (" + DiskLayout.Human(disk.BlockCount * 512) + ")"
            + (existing > 0 ? ", removing its " + existing + " partitions" : "");
        if (!apply) { return null; }
        Gpt.Create(disk);
        GptChecksums.Complete(disk);
        StorageManager.RescanPartitions(disk);
        Changed();
        Log.Write("disk", "wrote " + summary);
        return null;
    }

    public static string? Create(string diskName, ulong? sectors, bool ext2, string label, bool apply, out string summary, out string created)
    {
        summary = created = "";
        IBlockDevice? disk = Disk(diskName);
        if (disk is null) { return diskName + ": no such disk"; }
        if (Editable(disk) is string refused) { return refused; }
        if (DiskLayout.Place(disk.BlockCount, Table(disk), sectors) is not DiskRegion region) { return "no free region of that size on " + disk.Name; }
        bool live = AnythingMounted(disk);
        var temporary = new Partition(disk, region.Start, region.Sectors, disk.Name + "-new");
        if (ext2 && Ext2Formatter.Check(temporary, label) is string invalid) { return invalid; }
        summary = "partition of " + DiskLayout.Human(region.Sectors * 512) + " at sector " + region.Start + " on " + disk.Name
            + (ext2 ? ", formatted ext2" + (label.Length > 0 ? " \"" + label + "\"" : "") : ", unformatted");
        if (!apply) { return null; }
        if (!Gpt.AddPartition(disk, region.Start, region.Sectors, LinuxFilesystem)) { return "the partition table refused the new entry (no free slot?)"; }
        GptChecksums.Complete(disk);
        Partition target = temporary;
        if (!live)
        {
            StorageManager.RescanPartitions(disk);
            foreach (var p in StorageManager.GetPartitions(disk)) { if (p.StartSector == region.Start) { target = p; created = p.Name; } }
        }
        if (ext2) { Ext2Formatter.Format(target, label); }
        Changed();
        if (live) { summary += "; the system disk is in use, so the partition appears after a restart"; }
        Log.Write("disk", "created " + summary);
        return null;
    }

    public static string? Delete(Partition? target, bool apply, out string summary)
    {
        summary = "";
        if (target is null) { return "no such partition"; }
        IBlockDevice disk = target.Host;
        if (Editable(disk) is string refused) { return refused; }
        if (AnythingMounted(disk)) { return disk.Name + " has mounted partitions; unmount them (or use the rescue system for the system disk)"; }
        int index = Table(disk).FindIndex(p => p.Start == target.StartSector);
        if (index < 0) { return "partition not found in the table"; }
        summary = target.Name + " (" + DiskLayout.Human(target.BlockCount * target.BlockSize) + ", " + Probe(target).Filesystem + ")";
        if (!apply) { return null; }
        if (!Gpt.RemovePartition(disk, index)) { return "the partition table refused the change"; }
        GptChecksums.Complete(disk);
        StorageManager.RescanPartitions(disk);
        Changed();
        Log.Write("disk", "deleted " + summary);
        return null;
    }

    public static string? Grow(Partition? target, ulong? sectors, bool apply, out string summary)
    {
        summary = "";
        if (target is null) { return "no such partition"; }
        IBlockDevice disk = target.Host;
        if (Editable(disk) is string refused) { return refused; }
        if (AnythingMounted(disk))
        {
            return disk.Name + " has mounted partitions; unmount them"
                + (IsSystemDisk(disk) ? " (the system disk is grown from the rescue system: choose it in the boot menu)" : "");
        }
        var table = Table(disk);
        var entry = table.Find(p => p.Start == target.StartSector);
        if (entry.Sectors == 0) { return "partition not found in the table"; }
        ulong limit = DiskLayout.GrowLimit(disk.BlockCount, table, entry), wanted = sectors ?? limit;
        if (wanted <= entry.Sectors) { return target.Name + " is already " + DiskLayout.Human(entry.Sectors * 512) + "; shrinking is not supported"; }
        if (wanted > limit) { return target.Name + " can grow to at most " + DiskLayout.Human(limit * 512) + " (the next partition or the disk end)"; }
        var grown = new Partition(disk, entry.Start, wanted, target.Name);
        if (Ext2Resizer.Plan(grown, out var before, out var after) is string fsPlan) { return "cannot grow the ext2 volume on " + target.Name + ": " + fsPlan; }
        summary = target.Name + " from " + DiskLayout.Human(entry.Sectors * 512) + " to " + DiskLayout.Human(wanted * 512)
            + " (ext2 " + before.Groups + " -> " + after.Groups + " groups)";
        if (!apply) { return null; }
        if (!Gpt.ResizePartition(disk, table.FindIndex(p => p.Start == entry.Start), wanted)) { return "the partition table refused the change"; }
        GptChecksums.Complete(disk);
        StorageManager.RescanPartitions(disk);
        Changed();
        Partition? resized = Find(disk.Name, entry.Start);
        if (resized is null) { return "partition missing after the table change; the volume was not grown"; }
        Ext2Resizer.Grow(resized);
        bool clean = Ext2Checker.Check(resized, out string report);
        Log.Write("disk", "grew " + summary + "; check: " + report);
        if (!clean) { return "grew " + summary + ", but the check failed: " + report; }
        summary += "; check clean";
        return null;
    }

    // ---------- Background jobs for the ABI: one at a time, run as root on their own thread.

    public enum JobState { Idle = 0, Running = 1, Done = 2, Failed = 3 }
    public enum Action { Init = 1, Create = 2, Delete = 3, Grow = 4 }
    // Job fields have their own lock: an edit holds Gate for its whole run, and clients poll meanwhile.
    private static readonly object s_jobLock = new();
    private static JobState s_jobState;
    private static Action s_jobAction;
    private static ulong s_jobToken;
    private static string s_jobMessage = "";
    public static (JobState State, Action Action, ulong Token, string Message) Job
    {
        get { lock (s_jobLock) { return (s_jobState, s_jobAction, s_jobToken, s_jobMessage); } }
    }

    /// <summary>Starts an edit in the background; returns its token (positive) or a negative error.</summary>
    public static long Submit(uint uid, Action action, ulong revision, string disk, ulong start, ulong sectors, bool ext2, string label)
    {
        if (uid != 0) { return -13; }
        lock (s_jobLock)
        {
            if (s_jobState == JobState.Running) { return -16; }
        }
        if (!Monitor.TryEnter(Gate)) { return -16; }   // a disk command is running
        try
        {
            if (revision != Revision) { return -11; }
            // Refuse what cannot work before starting: the same checks, without writing.
            string? error = action switch
            {
                Action.Init => Init(disk, false, out _),
                Action.Create => Create(disk, sectors == 0 ? null : sectors, ext2, label, false, out _, out _),
                Action.Delete => Delete(Find(disk, start), false, out _),
                Action.Grow => Grow(Find(disk, start), sectors == 0 ? null : sectors, false, out _),
                _ => "unknown action",
            };
            ulong token;
            lock (s_jobLock)
            {
                if (s_jobState == JobState.Running) { return -16; }
                token = ++s_jobToken; s_jobAction = action;
                if (error is not null) { s_jobState = JobState.Failed; s_jobMessage = error; return (long)token; }
                s_jobState = JobState.Running; s_jobMessage = "";
            }
            var worker = new Thread(() => RunJob(token, action, disk, start, sectors, ext2, label));
            worker.Start();
            return (long)token;
        }
        finally { Monitor.Exit(Gate); }
    }

    private static void RunJob(ulong token, Action action, string disk, ulong start, ulong sectors, bool ext2, string label)
    {
        var root = CredentialContext.Enter(Credentials.Root);
        string? error; string summary = "";
        try
        {
            lock (Gate)
            {
                error = action switch
                {
                    Action.Init => Init(disk, true, out summary),
                    Action.Create => Create(disk, sectors == 0 ? null : sectors, ext2, label, true, out summary, out _),
                    Action.Delete => Delete(Find(disk, start), true, out summary),
                    Action.Grow => Grow(Find(disk, start), sectors == 0 ? null : sectors, true, out summary),
                    _ => "unknown action",
                };
            }
            root.Dispose();
        }
        catch (Exception e)
        {
            root.Dispose();
            error = e.Message;
        }
        lock (s_jobLock)
        {
            if (s_jobToken != token) { return; }
            s_jobState = error is null ? JobState.Done : JobState.Failed;
            s_jobMessage = error ?? summary;
        }
        Changed();
    }
}
