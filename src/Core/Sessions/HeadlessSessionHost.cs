using Zenith.Core.Console;
using Zenith.Core.Security;
using Zenith.Core.Storage;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Core.Sessions;

internal sealed class HeadlessSessionHost : ISessionHost
{
    private SerialTerminal _terminal = null!;
    private int _generation;
    public bool CanStop => !_terminal.Busy && !_terminal.Shell.HasBackgroundJobs;
    public string? StopBlocker(CommandShell? requester) => _terminal.Shell.HasBackgroundJobs ? "Background jobs are still running." : _terminal.Busy && !ReferenceEquals(requester, _terminal.Shell) ? "A command is still running." : null;
    public void Stop() { }
    public void BeginStop() => _terminal.CloseInput();
    public void Start()
    {
        _terminal = new SerialTerminal(output =>
        {
            _generation = Accounts.Generation;
            using var identity = CredentialContext.Enter(Accounts.Session);
            var shell = new CommandShell(output, login: false, metadata: CommandShell.DefaultMetadata);
            string name = Accounts.Service?.Name(Accounts.Session.Uid) ?? (Accounts.Session.IsRoot ? "root" : "nobody");
            shell.SetIdentity(Accounts.Session, name, Accounts.Session.IsRoot ? "/root" : name == "nobody" ? "/" : "/home/" + name);
            shell.Set("HOSTNAME", Zenith.Core.Network.NetworkService.Hostname);
            shell.Set("TERM", "vt100"); shell.Set("COLUMNS", "80");
            // These bootstrap messages contain no secrets and fit the bounded startup output.
            if (Accounts.Error is string error) { output.Write("Accounts unavailable: " + error + "\n"); }
            else if (Accounts.Service is not null && !Accounts.Service.HasPasswords)
            { output.Write("Initial root setup: passwd root, passwd user, then login user.\n"); }
            else if (Accounts.Session.Uid == 65534) { output.Write("Use login USER to authenticate.\n"); }
            return shell;
        }, refresh: () => _generation != Accounts.Generation,
            endSession: () => { if (Accounts.Service is not null) { Accounts.Logout(); } });
        Log.Write("kernel", "headless ready (" + (SystemMounts.Mode == BootMode.Live ? "live" : "installed") + "); no desktop initialization");
    }
    public void Tick()
    {
        _terminal.Tick();
        for (int i = 0; i < 64; i++)
        {
            int value = SerialPort.Read();
            if (value == -1) { break; }
            if (value == -2) { _terminal.Receive(3); Log.Write("console", "UART receive error; input cancelled"); break; }
            _terminal.Receive((byte)value);
        }
        string output = _terminal.Drain();
        if (output.Length > 0) { Log.WriteSerial(output); }
    }
}
