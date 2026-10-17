using System;
using System.Linq;
using Zenith.Core.Processes;
using Zenith.Core.IO;

namespace Zenith.Core.Shell.Commands;

internal static class NativeProcessCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("native", "native check FILE | run NAME [ARGS...]", "Inspect or run a packaged x86_64 native text program", Run));
        add(new Command("sunbeam", "sunbeam [cat|tee|wc|head|tail|ls|pwd|stat|echo|id|basename|dirname] [ARGS...]", "Run independently packaged Sunbeam tools", c => Execute(c, "sunbeam", new[] { "sunbeam" }.Concat(c.Args).ToArray())));
    }
    private static int Run(CommandContext c)
    {
        if (c.Args.Length == 2 && c.Args[0] == "check")
        {
            var image = ElfImage.Read(new Zenith.Core.Packages.NativePackageStore().Read(c.Resolve(c.Args[1]), ElfImage.MaximumBytes));
            c.WriteLine("Zenith native ABI 1; x86_64 ELF entry=0x" + image.Entry.ToString("x") + " segments=" + image.Segments.Count); return 0;
        }
        if (c.Args.Length < 2 || c.Args[0] != "run") { return c.Fail("native check FILE | run NAME [ARGS...]"); }
        return Execute(c, c.Args[1], c.Args.Skip(1).ToArray());
    }
    private static int Execute(CommandContext c, string name, string[] arguments)
    {
        IByteInput input = c.Input ?? (IByteInput)new LineByteInput(() => c.Shell.ReadInputLine?.Invoke(""), () => c.Shell.IsCancelled);
        var stdout = new CommandByteOutput(c.Out); var stderr = new CommandByteOutput(c.Err);
        var streams = new ProcessStreams(input, (fd, bytes) => fd == 1 ? stdout.Write(bytes) : stderr.Write(bytes),
            c.Input is null && c.Shell.ReadInputLine is not null, c.IsTerminal, c.Err == c.Shell.Terminal);
        var result = NativeProcess.Run(name, arguments, () => c.Shell.IsCancelled, c, c.Shell.WorkingDirectory, streams: streams);
        if (!c.Shell.IsCancelled) { stdout.Complete(); stderr.Complete(); }
        if (result.Error.Length > 0) { c.Err.Write("native: " + result.Error + "\n"); }
        if (result.Fault != 0 && result.Status != 95) { c.Err.Write("native process terminated by CPU fault vector=" + result.Fault + "\n"); }
        if (result.Status == 124) { c.Err.Write("native process exceeded its execution budget\n"); }
        if (result.Status == 95) { c.Err.Write("native process unsupported by current CPU/kernel descriptor setup; check stage=" + result.Fault + "\n"); }
        return result.Status;
    }
}
