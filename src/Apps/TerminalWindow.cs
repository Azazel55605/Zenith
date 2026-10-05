using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Shell;
using Zenith.Gui;
using Zenith.Gui.Graphics;
using Zenith.Gui.Shell;
using Zenith.Gui.Terminal;

namespace Zenith.Apps;

/// <summary>
/// A terminal emulator window hosting a <see cref="Shell"/>. The scrollback is a
/// <see cref="TerminalBuffer"/>; the line being edited is kept separately and drawn after it.
/// Keys: arrows/Home/End edit, Up/Down history, Tab completes, Ctrl+C cancels, Ctrl+L clears,
/// Ctrl+U kills the line, Ctrl+D on an empty line exits; PageUp/PageDown or the wheel scroll.
/// <para>
/// Each command line runs on its own thread, so a long command never freezes the desktop.
/// Threading rules: the worker only touches the shell and <see cref="Write"/> (which queues
/// text under a lock); everything else, including the scrollback, history recall, completion
/// and window actions the shell asks for, stays on the GUI thread and is synced in
/// <see cref="Update"/>. Lines entered while a command runs are queued, like type-ahead.
/// </para>
/// </summary>
internal sealed class TerminalWindow : Window, IOutput
{
    private const int FontSize = 14;
    private const int Padding = 10;
    private const uint Background = 0xFF141720;

    private readonly TerminalBuffer _buffer;
    private readonly Shell _shell;
    private readonly int _cellWidth;
    private readonly int _cellHeight;
    private readonly int _rows;
    private readonly StringBuilder _input = new();
    private int _cursor;
    private int _historyIndex;
    private int _scroll;   // lines scrolled back from the bottom
    private bool _caretVisible = true;

    // Shared with the worker thread; guarded by _sync.
    private readonly object _sync = new();
    private readonly StringBuilder _pendingOutput = new();
    private readonly Queue<(string Name, string? Argument)> _pendingApps = new();
    private bool _exitRequested;

    private Thread? _job;
    private volatile bool _jobRunning;
    private readonly Queue<string> _typeAhead = new();
    private string _prompt;

    // A multi-line command being entered (shown with the continuation prompt).
    private readonly StringBuilder _pendingScript = new();

    // `read` waiting for a line from the user; guarded by _sync.
    private bool _readRequested;
    private string? _readReply;
    private bool _readEof;

    public TerminalWindow() : base("Terminal", 740, 470)
    {
        _cellWidth = Fonts.Mono.GetGlyph('M', FontSize).Advance;
        _cellHeight = Fonts.Mono.GetLineHeight(FontSize);
        int columns = (Bounds.W - 2 * Padding) / _cellWidth;
        _rows = (Bounds.H - TitleBarHeight - 2 * Padding) / _cellHeight;

        _buffer = new TerminalBuffer(columns);
        _shell = new Shell(this);
        _shell.Set("COLUMNS", columns.ToString());
        _shell.ExitRequested += () => { lock (_sync) { _exitRequested = true; } };
        _shell.OpenApp = QueueOpenApp;
        _shell.ReadInputLine = ReadLineForShell;
        _prompt = _shell.Prompt;

        if (File.Exists("/etc/motd"))
        {
            _buffer.Write(File.ReadAllText("/etc/motd"));
        }

        _buffer.Write("\n");
    }

    /// <summary>Opens a desktop app by name; set once by the desktop.</summary>
    public static Func<string, string?, bool>? OpenApp { get; set; }

    /// <summary>Whether a command line is running.</summary>
    public bool IsBusy => _jobRunning;

    // IOutput: everything the shell and its commands print lands here, from the worker thread.
    public void Write(string text)
    {
        lock (_sync)
        {
            _pendingOutput.Append(text);
        }
    }

    public override bool Update()
    {
        SyncWithJob();

        bool visible = !IsFocused || Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 2) % 2 == 0;
        bool changed = visible != _caretVisible || _buffer.Changed;
        _caretVisible = visible;
        _buffer.Changed = false;
        return changed;
    }

    /// <summary>Moves the worker's output and requests onto the GUI thread; finishes a completed job.</summary>
    private void SyncWithJob()
    {
        string output;
        (string Name, string? Argument)[] apps;
        bool exit;
        lock (_sync)
        {
            output = _pendingOutput.ToString();
            _pendingOutput.Clear();
            apps = _pendingApps.ToArray();
            _pendingApps.Clear();
            exit = _exitRequested;
        }

        if (output.Length > 0)
        {
            _buffer.Write(output);
        }

        foreach (var (name, argument) in apps)
        {
            OpenApp?.Invoke(name, argument);
        }

        if (exit)
        {
            Close();
            return;
        }

        if (_job is not null && !_jobRunning)
        {
            _job = null;

            // Keep the prompt on its own line even if the command's output did not end in one.
            if (_buffer.CursorColumn != 0)
            {
                _buffer.Write("\n");
            }

            _prompt = _shell.Prompt;
            _historyIndex = _shell.History.Count;
            Title = "Terminal — " + _shell.WorkingDirectory;
            _buffer.Changed = true;

            if (_typeAhead.Count > 0)
            {
                Accept(_typeAhead.Dequeue());
            }
        }
        else if (_job is not null && _typeAhead.Count > 0 && IsReadPending())
        {
            AnswerRead(_typeAhead.Dequeue());   // a line typed ahead answers `read`
        }
    }

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

    private bool IsReadPending()
    {
        lock (_sync)
        {
            return _readRequested && _readReply is null && !_readEof;
        }
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

    public override void OnScroll(int delta)
    {
        _scroll = Math.Clamp(_scroll + delta * 3, 0, Math.Max(0, _buffer.LineCount - 1));
        _buffer.Changed = true;
    }

    public override void OnKey(KeyEvent key)
    {
        bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (key.Key != ConsoleKeyEx.PageUp && key.Key != ConsoleKeyEx.PageDown)
        {
            _scroll = 0;
        }

        if (control)
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
            case ConsoleKeyEx.PageUp:
                OnScroll(_rows / 3);
                break;
            case ConsoleKeyEx.PageDown:
                OnScroll(-_rows / 3);
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
                    _shell.Cancel();
                    Write(Ansi.Dim("^C") + "\n");
                }
                else
                {
                    _buffer.Write(CurrentPrompt + _input + Ansi.Dim("^C") + "\n");
                    _pendingScript.Clear();
                }

                ResetInput();
                break;
            case ConsoleKeyEx.L:
                _buffer.Clear();
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
                    AnswerRead(null);   // end of input for `read`
                }
                else if (_input.Length == 0 && _pendingScript.Length == 0)
                {
                    Close();
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
            Write(line + "\n");   // echoed like a terminal in cooked mode
            if (!AnswerRead(line))
            {
                _typeAhead.Enqueue(line);
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
        _buffer.Write(CurrentPrompt + line + "\n");
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
        _jobRunning = true;
        _job = new Thread(() =>
        {
            try
            {
                _shell.Execute(line);
            }
            finally
            {
                _jobRunning = false;
            }
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
            _buffer.Write(CurrentPrompt + _input + "\n" + string.Join("  ", candidates) + "\n");
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
            _pendingApps.Enqueue((name, argument));
        }

        return true;
    }

    public override void DrawContent(Surface surface, Rect content)
    {
        surface.FillRect(content, Background);

        // The visible lines: the scrollback, then its unfinished last line (e.g. a `read -p`
        // prompt) continued by the shell prompt and the input, wrapped to the width. While a
        // command runs there is no shell prompt, just whatever is being typed.
        string prompt = IsBusy ? string.Empty : CurrentPrompt;
        List<Cell> tail = _buffer.Line(_buffer.LineCount - 1);
        var promptBuffer = new TerminalBuffer(_buffer.Columns);
        promptBuffer.AppendCells(tail);
        promptBuffer.Write(prompt + _input);
        int inputLines = promptBuffer.LineCount;
        int total = _buffer.LineCount - 1 + inputLines;   // the buffer's last line is the empty current line

        int bottom = total - _scroll;
        int first = Math.Max(0, bottom - _rows);
        int x0 = content.X + Padding, y = content.Y + Padding;

        for (int i = first; i < bottom; i++)
        {
            List<Cell> line = i < _buffer.LineCount - 1 ? _buffer.Line(i) : promptBuffer.Line(i - (_buffer.LineCount - 1));
            DrawCells(surface, line, x0, y);
            y += _cellHeight;
        }

        if (IsFocused && _caretVisible && _scroll == 0)
        {
            int promptLength = Ansi.Strip(prompt).Length;
            int position = tail.Count + promptLength + _cursor;
            int row = total - 1 - (inputLines - 1) + position / _buffer.Columns - first;
            int col = position % _buffer.Columns;
            surface.FillRect(new Rect(x0 + col * _cellWidth, content.Y + Padding + row * _cellHeight, 2, _cellHeight), Theme.Accent);
        }

        if (_scroll > 0)
        {
            string label = "↑ " + _scroll + " lines";
            int w = Fonts.Regular.MeasureString(label, Theme.TextSmall) + 16;
            Rect badge = new(content.Right - w - 10, content.Y + 8, w, 22);
            surface.FillRoundRect(badge, 11, Theme.AccentSoft);
            surface.DrawTextCentered(label, Fonts.Regular, Theme.TextSmall, Theme.TextPrimary, badge, true);
        }
    }

    /// <summary>Draws a row of cells, batching runs of the same color into one string.</summary>
    private void DrawCells(Surface surface, List<Cell> cells, int x, int y)
    {
        int start = 0;
        while (start < cells.Count)
        {
            uint color = cells[start].Color;
            int end = start;
            var run = new StringBuilder();
            while (end < cells.Count && cells[end].Color == color)
            {
                run.Append(cells[end].Char);
                end++;
            }

            surface.DrawText(run.ToString(), Fonts.Mono, FontSize, color, x + start * _cellWidth, y);
            start = end;
        }
    }
}
