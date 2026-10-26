using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core;
using Zenith.Core.Console;
using Zenith.Core.IO;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Gui.Graphics;
using Zenith.Gui.Terminal;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Gui.Shell;

internal sealed class LocalConsoleHost : ISessionHost
{
    private SerialTerminal _terminal = null!;
    private readonly TerminalBuffer _buffer = new();
    private Surface _surface = null!;
    private int _generation;
    public bool CanStop => !_terminal.Busy && !_terminal.Shell.HasBackgroundJobs;
    public string? StopBlocker(CommandShell? requester) => _terminal.Shell.HasBackgroundJobs || _terminal.Busy && !ReferenceEquals(requester, _terminal.Shell) ? "A command is still running." : null;
    public void Stop() { _surface = null!; }
    public void BeginStop() => _terminal.CloseInput();
    public void Start()
    {
        NativeAppearance.Reload();
        var screen = SessionDisplay.Screen;
        _surface = new Surface(screen.Width, screen.Height);
        _terminal = new SerialTerminal(output =>
        {
            _generation = Accounts.Generation;
            var shell = new CommandShell(output, login: false, metadata: CommandShell.DefaultMetadata);
            string name = Accounts.Service?.Name(Accounts.Session.Uid) ?? (Accounts.Session.IsRoot ? "root" : "nobody");
            shell.SetIdentity(Accounts.Session, name, Accounts.Home(Accounts.Session));
            shell.Set("HOSTNAME", Zenith.Core.Network.NetworkService.Hostname);
            shell.Set("TERM", "zenith-console"); shell.Set("COLUMNS", Math.Max(20, screen.Width / 9).ToString());
            return shell;
        }, refresh: () => _generation != Accounts.Generation, endSession: () => SessionRuntime.RequestLogout(), banner: "Local text console");
        _buffer.Write("Zenith local console — logout to choose a session. Up/Down: history.\n");
        Log.Write("kernel", "headless ready (local text console)");
    }
    public void Tick()
    {
        _terminal.Tick();
        while (KeyboardManager.TryReadKey(out KeyEvent? key))
        {
            bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
            if (_terminal.RawInput)
            {
                foreach (char value in TerminalKeyEncoding.Encode(key.Key.ToString(), key.KeyChar, control && !((key.Modifiers & ConsoleModifiers.Alt) != 0 && key.KeyChar >= ' '))) { _terminal.ReceiveCharacter(value); }
            }
            else if (key.Key == ConsoleKeyEx.UpArrow) { _terminal.Recall(true); }
            else if (key.Key == ConsoleKeyEx.DownArrow) { _terminal.Recall(false); }
            else if (key.Key == ConsoleKeyEx.Enter) { _terminal.Receive(13); }
            else if (key.Key == ConsoleKeyEx.Backspace) { _terminal.Receive(8); }
            else if (control && key.Key == ConsoleKeyEx.C) { _terminal.Receive(3); }
            else if (control && key.Key == ConsoleKeyEx.D) { _terminal.Receive(4); }
            else if (control && key.Key == ConsoleKeyEx.U) { _terminal.Receive(21); }
            else if (key.KeyChar >= ' ') { _terminal.ReceiveCharacter(key.KeyChar); }
        }
        string output = _terminal.Drain(4096);
        if (output.Length == 0) { return; }
        _buffer.Write(output); Log.WriteSerial(output);
        _surface.FillRect(_surface.Bounds, Theme.WindowBackground);
        int cell = Math.Max(1, Fonts.Mono.GetGlyph('M', 14).Advance);
        int height = Fonts.Mono.GetLineHeight(14) + 2;
        int columns = Math.Max(1, (_surface.Width - 24) / cell), rows = Math.Max(1, (_surface.Height - 24) / height);
        var visible = new List<List<Cell>>();
        for (int i = Math.Max(0, _buffer.LineCount - rows); i < _buffer.LineCount; i++) { visible.AddRange(TerminalBuffer.Wrap(_buffer.Line(i), columns)); }
        int top = Math.Max(0, visible.Count - rows);
        for (int line = top; line < visible.Count; line++)
        {
            var cells = visible[line];
            for (int col = 0; col < cells.Count; col++) { _surface.DrawText(cells[col].Char.ToString(), Fonts.Mono, 14, cells[col].Color, 12 + col * cell, 12 + (line - top) * height); }
        }
        var screen = SessionDisplay.Screen; screen.DrawArray(_surface.Pixels, 0, 0, _surface.Width, _surface.Height); screen.Display();
    }
}
