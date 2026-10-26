using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Zenith.Core.Processes;
using Zenith.Core.IO;

namespace Zenith.Core.Shell.Commands;

internal static class NativeProcessCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("ps", "ps", "List native processes visible to the invoking account", c =>
        {
            if (c.Args.Length != 0) { return c.Fail("usage: ps"); }
            c.WriteLine("PID UID STATE STATUS PROGRAM");
            foreach (var process in NativeProcess.Processes.List(c.Shell.Identity.Uid))
            { c.WriteLine(process.Pid + " " + process.Uid + " " + process.State.ToString().ToLowerInvariant() + " " + (process.Status?.ToString() ?? "-") + " " + process.Name); }
            return 0;
        }));
        add(new Command("kill", "kill PID | %JOB", "Request cancellation of an owned native process or session job", c =>
        {
            if (c.Args.Length == 1 && c.Args[0].StartsWith('%'))
            { return int.TryParse(c.Args[0][1..], out int job) && c.Shell.StopJob(job) ? 0 : c.Fail("no running job"); }
            if (c.Args.Length != 1 || !int.TryParse(c.Args[0], out int pid) || pid <= 0) { return c.Fail("usage: kill PID | %JOB"); }
            int result = NativeProcess.Processes.RequestStop(pid, c.Shell.Identity.Uid);
            return result == 0 ? 0 : c.Fail(result == -13 ? "permission denied" : "no running process");
        }));
        add(new Command("native", "native check FILE | run NAME [ARGS...]", "Inspect or run a packaged x86_64 native text program", Run));
        add(new Command("sunbeam", "sunbeam [cat|tee|wc|head|tail|ls|pwd|stat|echo|id|basename|dirname] [ARGS...]", "Run independently packaged Sunbeam tools", c => Execute(c, "sunbeam", new[] { "sunbeam" }.Concat(c.Args).ToArray())));
        add(new Command("base-tools", "base-tools COMMAND [ARGS...]", "Run signed base tools", c => Execute(c, "base-tools", new[] { "base-tools" }.Concat(c.Args).ToArray())));
        add(new Command("fs-tools", "fs-tools COMMAND [ARGS...]", "Run signed filesystem tools", c => Execute(c, "fs-tools", new[] { "fs-tools" }.Concat(c.Args).ToArray())));
        add(new Command("net-tools", "net-tools COMMAND [ARGS...]", "Run signed network clients", c => Execute(c, "net-tools", new[] { "net-tools" }.Concat(c.Args).ToArray())));
        RegisterPackagedCommands(add);

    }
    private static void RegisterPackagedCommands(Action<Command> add)
    {
        // Builtins remain explicitly reachable for rescue. An installed but changed
        // executable must fail signed verification rather than silently fall back.
        var recovery = new Dictionary<string, Command>(Shell.Commands);
        add(new Command("recovery", "recovery COMMAND [ARGS...]", "Run a trusted recovery builtin", c =>
        {
            if (c.Args.Length == 0 || !recovery.TryGetValue(c.Args[0], out var command)) { return c.Fail("unknown recovery builtin"); }
            return command.Run(new CommandContext(c.Shell, c.Args[0], c.Args.Skip(1).ToArray(), c.Input, c.Out, c.Err, c.IsTerminal));
        }));
        foreach (var group in new[] {
            (Program:"base-tools",Names:new[] {"cat","tee","wc","head","tail","echo","id","basename","dirname","grep","sort","uniq","seq","printf","tr","cut","sed","diff"}),
            (Program:"fs-tools",Names:new[] {"mkdir","touch","rm","rmdir","cp","mv","chmod","chown","ln","ls","stat","pwd","find","tree","du"}),
            (Program:"net-tools",Names:new[] {"ip","ifconfig","nslookup","ping","nc","fetch"}) })
        {
            foreach (string name in group.Names)
            {
                if (!recovery.TryGetValue(name, out var fallback)) { continue; }
                string program = group.Program;
                string path = "/usr/share/zenith/native/" + program + ".elf";
                add(new Command(name, fallback.Usage, "Packaged " + program + " command; recovery builtin when absent", c =>
                {
                    if (Zenith.Core.Packages.NativePackageSource.State(path)==Zenith.Core.Packages.PackagePathState.Missing) { return fallback.Run(c); }
                    return Execute(c, program, new[] { program, name }.Concat(c.Args).ToArray());
                },()=>Zenith.Core.Packages.NativePackageSource.State(path)==Zenith.Core.Packages.PackagePathState.Missing ? "recovery builtin" : "signed native "+program));
            }
        }
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
        var cancellation = new ProcessCancellation(c.Shell.CancelForeground);
        IByteInput input = c.Input ?? (IByteInput?)c.Shell.NativeTerminal?.Acquire(() => c.Shell.IsCancelled || cancellation.Requested)
            ?? new LineByteInput(() => c.Shell.ReadInputLine?.Invoke(""), () => c.Shell.IsCancelled || cancellation.Requested);
        var stdout = new CommandByteOutput(c.Out); var stderr = new CommandByteOutput(c.Err);
        var streams = new ProcessStreams(input, (fd, bytes) => fd == 1 ? stdout.Write(bytes) : stderr.Write(bytes),
            c.Input is null && c.Shell.ReadInputLine is not null, c.IsTerminal, c.Err == c.Shell.Terminal);
        (int Status, string Error, ulong Fault) result;
        try
        {
            result = NativeProcess.Run(name, arguments, () => c.Shell.IsCancelled || cancellation.Requested,
                c, c.Shell.WorkingDirectory, streams: streams, environment: c.Shell.ExportedEnvironment,
                requestStop: cancellation.Request);
            streams.Close(); cancellation.Close();
        }
        catch { streams.Close(); cancellation.Close(); throw; }
        if (!c.Shell.IsCancelled) { stdout.Complete(); stderr.Complete(); }
        if (result.Error.Length > 0) { c.Err.Write("native: " + result.Error + "\n"); }
        if (result.Fault != 0 && result.Status != 95) { c.Err.Write("native process terminated by CPU fault vector=" + result.Fault + "\n"); }
        if (result.Status == 124) { c.Err.Write("native process exceeded its execution budget\n"); }
        if (result.Status == 95) { c.Err.Write("native process unsupported by current CPU/kernel descriptor setup; check stage=" + result.Fault + "\n"); }
        return result.Status;
    }
}
