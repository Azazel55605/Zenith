using System;
using System.Text;
using System.Threading.Tasks;
using Zenith.Core.Console;
using Zenith.Core.Security;
using Zenith.Core.Shell;
using Xunit;

namespace Zenith.Tests;

public class SerialTerminalTests
{
    private static SerialTerminal Terminal() => new(output => new Shell(output, login: false));
    private static void Send(SerialTerminal terminal, string text)
    { foreach (byte value in Encoding.ASCII.GetBytes(text)) { terminal.Receive(value); } }
    private static async Task<string> Until(SerialTerminal terminal, Func<string, bool> ready)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            terminal.Tick(); text.Append(terminal.Drain());
            if (ready(text.ToString())) { return text.ToString(); }
            await Task.Delay(10);
        }
        terminal.Receive(3);
        throw new TimeoutException(text.ToString());
    }
    [Fact]
    public async Task CrLfEditingAndQueuedCommandsKeepOneShell()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "echo firstx\b\r\necho second\n");
        string text = await Until(terminal, x => x.Contains("second\n") && !terminal.Busy);
        Assert.Contains("first\n", text); Assert.Contains("second\n", text);
        Assert.Equal(2, terminal.Shell.History.Count);
    }
    [Fact]
    public async Task MultilineScriptExecutesOnceAndKeepsStatus()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "if true; then\necho script-result\nfi\n");
        string text = await Until(terminal, x => x.Contains("script-result\n") && !terminal.Busy);
        Assert.Contains("script-result\n", text); Assert.Single(terminal.Shell.History);
        Assert.Equal(0, terminal.Shell.LastStatus);
    }
    [Fact]
    public async Task CtrlCCancelsWorkerAndDiscardsTypeAhead()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "sleep 30\n");
        await Task.Delay(100);
        Send(terminal, "echo should-not-run\n"); terminal.Receive(3);
        await Until(terminal, _ => !terminal.Busy);
        Assert.Equal(130, terminal.Shell.LastStatus); Assert.Single(terminal.Shell.History);
        Send(terminal, "echo after-cancel\n");
        Assert.Contains("after-cancel\n", await Until(terminal, x => x.Contains("after-cancel\n") && !terminal.Busy));
    }
    [Fact]
    public async Task ReadAndEofUseInteractiveInput()
    {
        var terminal = Terminal(); terminal.Drain();
        // Assemble prompts from adjacent quoted fragments so the input echo
        // cannot satisfy the wait before the worker actually starts reading.
        Send(terminal, "read -p 'in''put: ' VALUE; echo got=$VALUE\n");
        await Until(terminal, x => x.Contains("input: "));
        Send(terminal, "answer\n");
        Assert.Contains("got=answer\n", await Until(terminal, x => x.Contains("got=answer\n") && !terminal.Busy));
        Send(terminal, "read -p 'e''of: ' VALUE\n"); await Until(terminal, x => x.Contains("eof: "));
        terminal.Receive(4); await Until(terminal, _ => !terminal.Busy);
        Assert.Equal(1, terminal.Shell.LastStatus);
    }
    [Fact]
    public async Task HiddenSecretNeverEntersEchoOrHistory()
    {
        var terminal = Terminal(); terminal.Drain();
        // Exercise the same callback used by passwd/login without a host account database.
        Shell.Register(new Command("serial-secret-test", "serial-secret-test", "test", c =>
        { string? secret = c.Shell.ReadSecret!("Secret: "); c.WriteLine(secret == "private-value" ? "accepted" : "rejected"); return 0; }));
        Send(terminal, "serial-secret-test\n"); await Until(terminal, x => x.Contains("Secret: "));
        Send(terminal, "private-value\n"); string text = await Until(terminal, x => x.Contains("accepted") && !terminal.Busy);
        Assert.DoesNotContain("private-value", text);
        Assert.DoesNotContain(terminal.Shell.History, x => x.Contains("private-value"));
    }
    [Fact]
    public void OversizedLineIsDiscardedRatherThanExecutedAsPrefix()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "echo unsafe" + new string('x', SerialTerminal.MaximumLine) + "\n");
        string text = terminal.Drain(SerialTerminal.MaximumOutput);
        Assert.Contains("line too long; discarded", text); Assert.False(terminal.Busy); Assert.Empty(terminal.Shell.History);
    }
    [Fact]
    public async Task OutputIsBoundedWithBackpressureAndCancellation()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "while true; do echo output-flood; done\n"); await Task.Delay(100);
        Assert.True(terminal.Busy);
        Assert.InRange(terminal.Drain(int.MaxValue).Length, 1, SerialTerminal.MaximumOutput);
        terminal.Receive(3); await Until(terminal, _ => !terminal.Busy);
        Assert.Equal(130, terminal.Shell.LastStatus);
    }
    [Fact]
    public async Task ExitRecreatesShellAndDropsQueuedCommands()
    {
        var terminal = Terminal(); terminal.Drain(); var first = terminal.Shell;
        Send(terminal, "exit\necho stale\n");
        string text = await Until(terminal, x => x.Contains("session ended"));
        Assert.NotSame(first, terminal.Shell); Assert.Empty(terminal.Shell.History);
    }
    [Fact]
    public void NoDisplaySelectionFallsBackToHeadless()
    {
        var selected = Zenith.Core.Sessions.SessionModes.Select(Zenith.Core.Sessions.SessionMode.Desktop,
            "session=invalid", mode => mode == Zenith.Core.Sessions.SessionMode.Headless);
        Assert.Equal(Zenith.Core.Sessions.SessionMode.Headless, selected.Mode);
        Assert.Equal("capability fallback", selected.Source); Assert.Contains("using headless", selected.Diagnostic);
    }
    [Fact]
    public async Task QueueOverflowIsReportedAndCancelledLinesNeverExecute()
    {
        var terminal = Terminal(); terminal.Drain(); Send(terminal, "sleep 30\n");
        await Task.Delay(100);
        for (int i = 0; i <= SerialTerminal.MaximumQueuedLines; i++) { Send(terminal, "echo stale\n"); }
        Assert.Contains("command queue full; discarded", terminal.Drain(SerialTerminal.MaximumOutput));
        terminal.Receive(3); await Until(terminal, _ => !terminal.Busy);
        Assert.Single(terminal.Shell.History);
    }
    [Fact]
    public void AccumulatedIncompleteScriptCannotExceedItsBound()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "'" + new string('x', SerialTerminal.MaximumLine - 1) + "\n");
        for (int i = 0; i < 3; i++) { Send(terminal, new string('x', SerialTerminal.MaximumLine) + "\n"); }
        Assert.Contains("script too long; discarded", terminal.Drain(SerialTerminal.MaximumOutput));
        Assert.False(terminal.Busy); Assert.Empty(terminal.Shell.History);
    }
    [Fact]
    public async Task IdleCtrlCClearsInputWithoutCancellingTheNextCommand()
    {
        var terminal = Terminal(); terminal.Drain();
        Send(terminal, "echo incomplete"); terminal.Receive(3);
        Send(terminal, "echo fresh-command\n");
        Assert.Contains("fresh-command\n", await Until(terminal, x => x.Contains("fresh-command\n") && !terminal.Busy));
        Assert.Equal(0, terminal.Shell.LastStatus); Assert.Single(terminal.Shell.History);
    }
    [Fact]
    public async Task SessionGenerationChangeClearsEnvironmentHistoryAndQueuedCommands()
    {
        int generation = 0, loaded = 0;
        var terminal = new SerialTerminal(output => { loaded = generation; return new Shell(output, login: false); },
            refresh: () => loaded != generation);
        terminal.Drain(); var previous = terminal.Shell; previous.Set("PRIVATE", "prior-session");
        Shell.Register(new Command("serial-session-test", "serial-session-test", "test", _ => { generation++; return 0; }));
        Send(terminal, "serial-session-test\necho stale\n");
        await Until(terminal, x => x.Contains("session changed"));
        Assert.NotSame(previous, terminal.Shell); Assert.Equal("", terminal.Shell.Get("PRIVATE"));
        Assert.Empty(terminal.Shell.History);
    }
}
