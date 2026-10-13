using System;
using System.Linq;
using Zenith.Core.Processes;

namespace Zenith.Core.Shell.Commands;

internal static class NativeProcessCommands
{
    public static void Register(Action<Command> add)
    { add(new Command("native", "native check FILE | run NAME [ARGS...]", "Inspect or run a packaged x86_64 native text program", Run)); }
    private static int Run(CommandContext c)
    {
        if (c.Args.Length == 2 && c.Args[0] == "check")
        {
            var image = ElfImage.Read(new Zenith.Core.Packages.NativePackageStore().Read(c.Resolve(c.Args[1]), ElfImage.MaximumBytes));
            c.WriteLine("Zenith native ABI 1; x86_64 ELF entry=0x" + image.Entry.ToString("x") + " segments=" + image.Segments.Count); return 0;
        }
        if (c.Args.Length < 2 || c.Args[0] != "run") { return c.Fail("native check FILE | run NAME [ARGS...]"); }
        var result = NativeProcess.Run(c.Args[1], c.Args.Skip(1).ToArray(), c.Shell.ThrowIfCancelled, c);
        c.Out.Write(result.Output);
        if (result.Fault != 0 && result.Status != 95) { c.WriteLine("native process terminated by CPU fault vector=" + result.Fault); }
        if (result.Status == 124) { c.WriteLine("native process exceeded its interrupt budget"); }
        if (result.Status == 95) { c.WriteLine("native process unsupported by current CPU/kernel descriptor setup; check stage=" + result.Fault + ""); }
        return result.Status;
    }
}
