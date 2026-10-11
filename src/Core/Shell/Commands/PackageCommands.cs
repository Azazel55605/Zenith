using System;
using System.IO;
using Zenith.Core.Packages;

namespace Zenith.Core.Shell.Commands;

internal static class PackageCommands
{
    public static void Register(Action<Command> add)
    { add(new Command("pkg", "pkg list | inspect MANIFEST | plan MANIFEST", "Experimental offline package inspection and dry-run", Run)); }
    private static int Run(CommandContext c)
    {
        if (c.Args.Length == 1 && c.Args[0] == "list")
        {
            c.WriteLine("bundled image components (no installed package database):");
            foreach (var item in BootstrapComponents.All) { c.WriteLine(item.Name + " " + item.Version + " [" + item.Activation + "]"); }
            return 0;
        }
        if (c.Args.Length != 2 || !(c.Args[0] is "inspect" or "plan"))
        { return c.Fail("P0 supports list, inspect MANIFEST and plan MANIFEST; install/update are unavailable"); }
        string path = c.Resolve(c.Args[1]); var manifest = NativePackageSource.Load(path);
        c.WriteLine("experimental data package " + manifest.Name + " " + manifest.Version + " arch=" + manifest.Architecture);
        c.WriteLine("files=" + manifest.Files.Count + " payload=" + manifest.PayloadBytes + " runtime=" + manifest.RuntimeId);
        if (c.Args[0] == "inspect") { return 0; }
        var plan = PackagePlanner.Plan(manifest, new NativePackageSource(Path.GetDirectoryName(path) ?? "/"),
            "x86_64", BootstrapComponents.All, NativePackageSource.State);
        foreach (string action in plan.Actions) { c.WriteLine(action); }
        foreach (string blocker in plan.Blockers) { c.WriteLine("blocked: " + blocker); }
        c.WriteLine("dry-run only; integrity hashes are not signatures; no files changed");
        return plan.Applicable ? 0 : 1;
    }
}
