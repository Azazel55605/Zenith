using System;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Shell.Commands;

/// <summary>
/// <c>disk</c>: list disks, partitions, filesystems and free space; create, delete and grow GPT
/// partitions. The rules live in <see cref="DiskService"/>, shared with Equinox. Edits need root
/// and <c>--yes</c>; without it the change is only described.
/// </summary>
internal static class DiskCommands
{
    private const string Usage = "disk [list] | init DISK [--yes] | create DISK SIZE|rest [ext2|none] [-L LABEL] [--yes] | delete PARTITION [--yes] | grow PARTITION [SIZE|max] [--yes]";

    public static void Register(Action<Command> add)
        => add(new Command("disk", Usage, "Disks, partitions and free space; create, delete and grow partitions", Run));

    private static int Run(CommandContext c)
    {
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "list")) { return List(c); }
        GuardedFilesystem.RequireRoot();
        lock (DiskService.Gate)
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

    private static int List(CommandContext c)
    {
        var disks = DiskService.Describe();
        foreach (var disk in disks)
        {
            c.WriteLine(disk.Name + "  " + DiskLayout.Human(disk.Sectors * 512) + "  " + (disk.Gpt ? "GPT" : "no GPT (read-only here)")
                + (disk.System ? "  system disk" : ""));
            foreach (var p in disk.Parts)
            {
                string fs = p.Filesystem switch
                {
                    DiskFilesystem.Ext2 => "ext2 " + p.BlockSize / 1024 + "K" + (p.Label.Length > 0 ? " \"" + p.Label + "\"" : ""),
                    DiskFilesystem.Fat32 => "fat32",
                    _ => "-",
                };
                string type = p.Type switch { DiskPartitionType.Efi => "EFI", DiskPartitionType.Linux => "Linux", _ => "other" };
                string role = p.Role switch { DiskRole.Root => " (root)", DiskRole.Boot => " (boot)", _ => "" };
                c.WriteLine("  " + p.Name.PadRight(10) + DiskLayout.Human(p.Sectors * 512).PadLeft(10) + "  start " + p.Start.ToString().PadRight(10)
                    + type.PadRight(6) + fs.PadRight(18) + p.Mount + role);
            }
            foreach (var region in disk.Free)
            {
                c.WriteLine("  " + "free".PadRight(10) + DiskLayout.Human(region.Sectors * 512).PadLeft(10) + "  start " + region.Start);
            }
        }
        if (disks.Count == 0) { c.WriteLine("(no disks)"); }
        return 0;
    }

    private static int Finish(CommandContext c, string? error, string verb, string summary, bool apply, string confirm)
    {
        if (error is not null) { return c.Fail(error); }
        if (!apply) { c.WriteLine("would " + verb + " " + summary); return c.Fail(confirm); }
        c.WriteLine(verb switch { "write" => "wrote ", "create" => "created ", "delete" => "deleted ", _ => "grew " } + summary);
        return 0;
    }

    private static int Init(CommandContext c)
    {
        if (c.Args.Length is < 2 or > 3 || (c.Args.Length == 3 && c.Args[2] != "--yes")) { return c.Fail("usage: disk init DISK [--yes]"); }
        bool apply = c.Args.Length == 3;
        string? error = DiskService.Init(c.Args[1], apply, out string summary);
        return Finish(c, error, "write", summary, apply, "pass --yes to write it");
    }

    private static int Create(CommandContext c)
    {
        if (c.Args.Length < 3) { return c.Fail("usage: disk create DISK SIZE|rest [ext2|none] [-L LABEL] [--yes]"); }
        bool yes = false, ext2 = true; string label = "";
        for (int i = 3; i < c.Args.Length; i++)
        {
            if (c.Args[i] == "--yes") { yes = true; }
            else if (c.Args[i] is "ext2" or "none") { ext2 = c.Args[i] == "ext2"; }
            else if (c.Args[i] == "-L" && i + 1 < c.Args.Length) { label = c.Args[++i]; }
            else { return c.Fail("unexpected argument: " + c.Args[i]); }
        }
        ulong? sectors = c.Args[2] == "rest" ? null : DiskLayout.ParseSize(c.Args[2]);
        if (c.Args[2] != "rest" && sectors is null) { return c.Fail("size must be whole MiB, at least 1M (e.g. 512M, 20G) or rest"); }
        string? error = DiskService.Create(c.Args[1], sectors, ext2, label, yes, out string summary, out string created);
        if (error is null && yes && created.Length > 0) { summary = created + ": " + summary; }
        return Finish(c, error, "create", summary, yes, "pass --yes to create it");
    }

    private static int Delete(CommandContext c)
    {
        if (c.Args.Length is < 2 or > 3 || (c.Args.Length == 3 && c.Args[2] != "--yes")) { return c.Fail("usage: disk delete PARTITION [--yes]"); }
        bool apply = c.Args.Length == 3;
        string? error = DiskService.Delete(DiskService.Find(c.Args[1]), apply, out string summary);
        return Finish(c, error, "delete", summary, apply, "pass --yes to delete it");
    }

    private static int Grow(CommandContext c)
    {
        if (c.Args.Length is < 2 or > 4) { return c.Fail("usage: disk grow PARTITION [SIZE|max] [--yes]"); }
        bool yes = c.Args[^1] == "--yes";
        string size = c.Args.Length >= 3 && c.Args[2] != "--yes" ? c.Args[2] : "max";
        ulong? sectors = size == "max" ? null : DiskLayout.ParseSize(size);
        if (size != "max" && sectors is null) { return c.Fail("size must be whole MiB (e.g. 40G) or max"); }
        string? error = DiskService.Grow(DiskService.Find(c.Args[1]), sectors, yes, out string summary);
        return Finish(c, error, "grow", summary, yes, "pass --yes to grow it");
    }
}
