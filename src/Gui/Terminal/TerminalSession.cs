using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Shell;
using Zenith.Core.IO;
using Zenith.Core.Security;
using Zenith.Gui;
using Zenith.Gui.Graphics;
using Zenith.Gui.Shell;
using Zenith.Gui.Terminal;

namespace Zenith.Apps;

/// <summary>
/// One shell and everything that makes it interactive, without any drawing: the worker that runs commands,
/// the line being edited, history, type-ahead, hidden prompts and raw leases for native programs. Hosts feed it
/// keys and call <see cref="Sync"/> from their own thread; the worker only reaches them through the output queue.
/// </summary>
internal sealed class TerminalSession : IOutput
{
    private readonly TerminalBuffer _buffer = new();
    private readonly Shell _shell;
    private readonly TerminalInputChannel _nativeInput;
    private readonly Func<string, string?, bool> _openApp;
    private int _inputGeneration;
    private volatile bool _secretRead;
    private volatile bool _secretPrepared;
    private readonly StringBuilder _input = new();
    private int _cursor;
    private int _historyIndex;
    private string _historyDraft = "";

    // Shared with the worker thread; guarded by _sync.
    private readonly object _sync = new();
    private readonly StringBuilder _pendingOutput = new();
    private readonly Queue<(string Name, string? Argument, Credentials Identity)> _pendingApps = new();
    private bool _exitRequested;

    private Thread? _job;
    private volatile bool _jobRunning;
    private readonly TerminalTypeAhead _typeAhead = new();
    private string _prompt;
    private bool _newOutput;

    // A multi-line command being entered (shown with the continuation prompt).
    private readonly StringBuilder _pendingScript = new();

    // `read` waiting for a line from the user; guarded by _sync.
    private bool _readRequested;
    private string? _readReply;
    private bool _readEof;

    /// <param name="sink">Where output goes for a client that keeps its own grid; without one, <see cref="Buffer"/>.</param>
    public TerminalSession(Credentials identity, int columns, Func<string, string?, bool> openApp, Action<string>? sink = null)
    {
        _openApp = openApp;
        _sink = sink;
        _shell = new Shell(this);
        string user = Accounts.Service?.Name(identity.Uid) ?? (identity.IsRoot ? "root" : "nobody");
        _shell.SetIdentity(identity, user, identity.IsRoot ? "/root" : "/home/" + user);
        _shell.Set("COLUMNS", columns.ToString());
        _shell.ExitRequested += () => { lock (_sync) { _exitRequested = true; } };
        _shell.OpenApp = QueueOpenApp;
        _shell.ReadInputLine = ReadLineForShell;
        _shell.ReadSecret = ReadSecretForShell;
        _nativeInput = new TerminalInputChannel(() => ReadLineForShell(""), Write, () => _shell.CancelForeground());
        _shell.NativeTerminal = _nativeInput;
        _prompt = _shell.Prompt;

        if (File.Exists("/etc/motd"))
        {
            Emit(File.ReadAllText("/etc/motd"));
        }

        Emit("\n");
        if (Accounts.Error is string error) { Emit("Accounts unavailable: " + error + "\n"); }
        else if (Accounts.Service is not null && identity.IsRoot && !Accounts.Service.HasPasswords)
        { Emit("Initial root setup: run passwd root, then passwd user and login user.\n"); }
        else if (identity.Uid == 65534) { Emit("Use login USER to authenticate.\n"); }
    }

    public TerminalBuffer Buffer => _buffer;
    private readonly Action<string>? _sink;
    private bool _sinkAtLineStart = true;
    private void Emit(string text)
    {
        if (text.Length == 0) { return; }
        if (_sink is null) { _buffer.Write(text); return; }
        _sinkAtLineStart = text[^1] == '\n';
        _sink(text);
    }
    private bool AtLineStart => _sink is null ? _buffer.CursorColumn == 0 : _sinkAtLineStart;
    // Prompt marks for clients (OSC 133): where each command starts, and its status and time when it ends.
    // The built-in window has no use for them, so it never gets them.
    private long _commandStarted;
    private void Mark(string mark) { if (_sink is not null) { _sink("\u001b]133;" + mark + "\u0007"); } }
    public Credentials Identity => _shell.Identity;
    public string WorkingDirectory => _shell.WorkingDirectory;
    public bool IsBusy => _jobRunning;
    public bool HasBackgroundJobs => _shell.HasBackgroundJobs;
    public bool OwnsShell(Shell shell) => ReferenceEquals(_shell, shell);
    /// <summary>A hidden prompt is waiting: the input must not be shown, copied or put on a material.</summary>
    public bool SecretRead => _secretRead;
    public bool RawActive => _nativeInput.RawActive;
    /// <summary>The prompt for the line being edited; empty while a command runs.</summary>
    public string DisplayPrompt => IsBusy ? string.Empty : CurrentPrompt;
    public string InputText => _input.ToString();
    public int InputLength => _input.Length;
    public int Cursor => _cursor;
    /// <summary>The shell asked to end, or Ctrl+D on an empty line.</summary>
    public bool ExitRequested { get { lock (_sync) { return _exitRequested; } } }
    /// <summary>Set when a command finished since the last call: prompt, identity and directory may be new.</summary>
    public bool TakeJobFinished() { bool finished = _jobFinished; _jobFinished = false; return finished; }
    private bool _jobFinished;
    /// <summary>Set when output or a reset moved rows, so a host's selection no longer refers to the same text.</summary>
    public bool TakeRowsMoved() { bool moved = _newOutput; _newOutput = false; return moved; }

    public void Cancel()
    {
        _shell.CancelSession();
        lock (_sync) { _pendingApps.Clear(); }
    }

    // IOutput: everything the shell and its commands print lands here, from the worker thread.
    public void Write(string text)
    {
        lock (_sync)
        {
            _pendingOutput.Append(text);
        }
    }

    /// <summary>Takes the worker's output and requests onto the host's thread; finishes a completed job.</summary>
    public void Sync(int columns)
    {
        SyncNativeInput();
        if (_columns != columns) { _columns = columns; _columnsChanged = true; }
        if (_columnsChanged && !IsBusy)
        {
            _shell.Set("COLUMNS", _columns.ToString());
            _columnsChanged = false;
        }

        string output;
        if (_secretRead && !_secretPrepared)
        {
            ResetInput(); _typeAhead.Clear(); _newOutput = true; _secretPrepared = true;
        }
        else if (!_secretRead && _secretPrepared)
        {
            ResetInput(); _typeAhead.Clear(); _secretPrepared = false;
        }
        (string Name, string? Argument, Credentials Identity)[] apps;
        lock (_sync)
        {
            output = _pendingOutput.ToString();
            _pendingOutput.Clear();
            apps = _pendingApps.ToArray();
            _pendingApps.Clear();
        }

        if (output.Length > 0)
        {
            Emit(output);
            _newOutput = true;
        }

        foreach (var (name, argument, caller) in apps)
        {
            using var identity = CredentialContext.Enter(caller);
            _openApp(name, argument);
        }

        if (ExitRequested) { return; }

        if (_job is not null && !_jobRunning)
        {
            _job = null;

            // Keep the prompt on its own line even if the command's output did not end in one.
            if (!AtLineStart)
            {
                Emit("\n");
            }

            Mark("D;" + _shell.LastStatus + ";" + (Stopwatch.GetTimestamp() - _commandStarted) * 1000 / Stopwatch.Frequency);
            _prompt = _shell.Prompt;
            _historyIndex = _shell.History.Count;
            _jobFinished = true;
            _buffer.Changed = true;

            if (_typeAhead.TryTakeCommand(out string command))
            {
                Accept(command);
            }
        }
        else if (_job is not null && (!_secretRead || _secretPrepared) && _typeAhead.HasPending)
        {
            _typeAhead.TryAnswer(AnswerRead);
        }
    }
    private int _columns;
    private bool _columnsChanged;

    private string CurrentPrompt => _pendingScript.Length > 0 ? _shell.ContinuationPrompt : _prompt;

    /// <summary>
    /// Called by <c>read</c> on the worker thread: waits for the user's next line (Enter), end of
    /// input (Ctrl+D) or Ctrl+C. The GUI thread answers through <see cref="AnswerRead"/>.
    /// </summary>
    private string? ReadLineForShell(string prompt)
    {
        if (prompt.Length > 0)
        {
            Write(prompt);
        }

        lock (_sync)
        {
            _readRequested = true;
            _readReply = null;
            _readEof = false;
        }

        while (true)
        {
            lock (_sync)
            {
                if (_readReply is not null || _readEof)
                {
                    string? reply = _readEof ? null : _readReply;
                    _readRequested = false;
                    _readReply = null;
                    _readEof = false;
                    return reply;
                }
            }

            if (_shell.IsCancelled)
            {
                lock (_sync)
                {
                    _readRequested = false;
                }

                return null;
            }

            Thread.Sleep(20);
        }
    }

    private string? ReadSecretForShell(string prompt)
    {
        _secretRead = true;
        try
        {
            while (!_secretPrepared && !_shell.IsCancelled) { Thread.Sleep(20); }
            return _shell.IsCancelled ? null : ReadLineForShell(prompt);
        }
        finally { _secretRead = false; }
    }

    /// <summary>Hands a line (or end of input, when null) to a waiting <c>read</c>; returns false if none is waiting.</summary>
    private bool AnswerRead(string? line)
    {
        lock (_sync)
        {
            if (!_readRequested || _readReply is not null || _readEof)
            {
                return false;
            }

            if (line is null)
            {
                _readEof = true;
            }
            else
            {
                _readReply = line;
            }

            return true;
        }
    }

    /// <summary>Drops the edit line if a native program took or returned the terminal since the last look.</summary>
    public void SyncNativeInput()
    {
        int generation = _nativeInput.Generation;
        if (_inputGeneration == generation) { return; }
        ResetInput(); _typeAhead.Clear(); _newOutput = true; _inputGeneration = generation;
    }

    /// <summary>Raw bytes for a native program that holds a raw lease.</summary>
    public void KeyRaw(KeyEvent key)
    {
        _nativeInput.Receive(TerminalKeyEncoding.Encode(key.Key.ToString(), key.KeyChar, (key.Modifiers & ConsoleModifiers.Control) != 0 && !((key.Modifiers & ConsoleModifiers.Alt) != 0 && key.KeyChar >= ' ')));
        _buffer.Changed = true;
    }

    /// <summary>The mark an edit line shows for a pasted line break, which is one character like any other.</summary>
    public const char LineBreakMark = '\u00b6';
    /// <summary>What to show for the edit line: the text with its line breaks visible.</summary>
    public string DisplayInput => _input.ToString().Replace('\n', LineBreakMark);

    /// <summary>Inserts text into the edit line. Pasting never runs anything: line breaks stay in the line,
    /// visibly, until the person presses Enter, and other control characters are dropped.</summary>
    public void Paste(string text)
    {
        foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            char value = c == '\t' ? ' ' : c;
            if (value != '\n' && (value < ' ' || value == '\u007f')) { continue; }
            _input.Insert(_cursor++, value);
        }
    }

    /// <summary>A key for the line editor, with the host's own keys already taken out.</summary>
    public void Key(KeyEvent key)
    {
        bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (control && !((key.Modifiers & ConsoleModifiers.Alt) != 0 && key.KeyChar >= ' '))
        {
            HandleControl(key.Key);
            return;
        }

        switch (key.Key)
        {
            case ConsoleKeyEx.Enter:
                Submit();
                break;
            case ConsoleKeyEx.Backspace:
                if (_cursor > 0)
                {
                    _input.Remove(--_cursor, 1);
                }

                break;
            case ConsoleKeyEx.Delete:
                if (_cursor < _input.Length)
                {
                    _input.Remove(_cursor, 1);
                }

                break;
            case ConsoleKeyEx.LeftArrow:
                _cursor = Math.Max(0, _cursor - 1);
                break;
            case ConsoleKeyEx.RightArrow:
                _cursor = Math.Min(_input.Length, _cursor + 1);
                break;
            case ConsoleKeyEx.Home:
                _cursor = 0;
                break;
            case ConsoleKeyEx.End:
                _cursor = _input.Length;
                break;
            case ConsoleKeyEx.UpArrow:
                RecallHistory(-1);
                break;
            case ConsoleKeyEx.DownArrow:
                RecallHistory(+1);
                break;
            case ConsoleKeyEx.Tab:
                Complete();
                break;
            default:
                if (key.KeyChar >= ' ')
                {
                    _input.Insert(_cursor++, key.KeyChar);
                }

                break;
        }

        _buffer.Changed = true;
    }

    private void HandleControl(ConsoleKeyEx key)
    {
        switch (key)
        {
            case ConsoleKeyEx.C:
                if (IsBusy)
                {
                    _typeAhead.Clear();
                    _shell.CancelForeground();
                    Write(Ansi.Dim("^C") + "\n");
                }
                else
                {
                    Emit(CurrentPrompt + _input + Ansi.Dim("^C") + "\n");
                    _pendingScript.Clear();
                }

                ResetInput();
                break;
            case ConsoleKeyEx.L:
                if (_sink is null) { _buffer.Clear(); } else { Emit(Ansi.ClearScreen); }
                break;
            case ConsoleKeyEx.U:
                ResetInput();
                break;
            case ConsoleKeyEx.A:
                _cursor = 0;
                break;
            case ConsoleKeyEx.E:
                _cursor = _input.Length;
                break;
            case ConsoleKeyEx.D:
                if (_input.Length == 0 && IsBusy)
                {
                    _typeAhead.EndInput();   // preserve EOF behind an unconsumed line
                }
                else if (_input.Length == 0 && _pendingScript.Length == 0)
                {
                    _exitRequested = true;
                }

                break;
        }

        _buffer.Changed = true;
    }

    private void Submit()
    {
        string line = _input.ToString();
        _input.Clear();
        _cursor = 0;

        if (IsBusy)
        {
            // A running command reads a line at a time, also from a pasted block.
            foreach (string part in line.Split('\n'))
            {
                Write((_secretRead ? string.Empty : part) + "\n");
                _typeAhead.Enqueue(part);
            }

            return;
        }

        Accept(line);
    }

    /// <summary>
    /// Takes a line at the prompt: echoes it, and either waits for more lines (an unfinished
    /// <c>if</c>, loop, quote, or trailing <c>|</c>) or runs the whole command on a worker thread.
    /// </summary>
    private void Accept(string line)
    {
        if (_pendingScript.Length == 0) { Mark("A"); }
        Emit(CurrentPrompt + line + "\n");
        string text = _pendingScript.Length > 0 ? _pendingScript + "\n" + line : line;
        _pendingScript.Clear();

        if (!Shell.IsComplete(text))
        {
            _pendingScript.Append(text);
            return;
        }

        if (text.Trim().Length > 0)
        {
            Run(text);
        }
    }

    /// <summary>Runs a complete command on a worker thread.</summary>
    private void Run(string line)
    {
        _commandStarted = Stopwatch.GetTimestamp();
        _jobRunning = true;
        _job = new Thread(() =>
        {
            using var credentials = CredentialContext.Enter(_shell.Identity);
            // Explicit: Cosmos can skip unwind-finally, which would leave this Terminal busy forever.
            try { _shell.Execute(line); }
            catch (Exception e) { Zenith.Core.Log.Write("terminal", "command failed: " + e.Message); }
            _jobRunning = false;
        });
        _job.Start();
    }

    private void ResetInput()
    {
        _input.Clear();
        _cursor = 0;
        if (!IsBusy)
        {
            _historyIndex = _shell.History.Count;
        }
    }

    private void RecallHistory(int direction)
    {
        if (IsBusy)
        {
            return;   // the worker may be appending to the history
        }

        List<string> history = _shell.History;
        if (_historyIndex == history.Count && direction < 0)
        {
            _historyDraft = _input.ToString();
        }
        int index = Math.Clamp(_historyIndex + direction, 0, history.Count);
        if (index == _historyIndex)
        {
            return;
        }

        _historyIndex = index;
        _input.Clear();
        if (index < history.Count)
        {
            _input.Append(history[index]);
        }
        else { _input.Append(_historyDraft); }

        _cursor = _input.Length;
    }

    private void Complete()
    {
        if (IsBusy)
        {
            return;   // completion reads the shell's working directory, which the worker owns
        }

        string before = _input.ToString(0, _cursor);
        string insert = _shell.Complete(before, out List<string> candidates);
        if (insert.Length > 0)
        {
            _input.Insert(_cursor, insert);
            _cursor += insert.Length;
        }
        else if (candidates.Count > 1)
        {
            // Ambiguous: show the options under the current line, like bash's double-Tab.
            Emit(CurrentPrompt + _input + "\n" + string.Join("  ", candidates) + "\n");
        }
    }

    /// <summary>Called by <c>open</c> on the worker thread: checks the name, then leaves the opening to the GUI thread.</summary>
    private bool QueueOpenApp(string name, string? argument)
    {
        if (!AppRegistry.Exists(name))
        {
            return false;
        }

        lock (_sync)
        {
            _pendingApps.Enqueue((name, argument, CredentialContext.Current));
        }

        return true;
    }
}
