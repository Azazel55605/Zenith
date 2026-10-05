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
    private int _rows;
    private int _columns;
    private bool _columnsChanged;
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

    // What the last frame showed: one entry per visible row, and whether that row's logical line
    // continues on the next row (a wrap, not a line break). Mouse selection works on these rows.
    private readonly List<(List<Cell> Cells, bool Continues)> _visibleRows = new();
    private (int Row, int Column)? _selectionAnchor;
    private (int Row, int Column) _selectionHead;
    private const uint SelectionColor = 0x557C9CFF;

    // `read` waiting for a line from the user; guarded by _sync.
    private bool _readRequested;
    private string? _readReply;
    private bool _readEof;

    public TerminalWindow() : base("Terminal", 740, 470)
    {
        _cellWidth = Fonts.Mono.GetGlyph('M', FontSize).Advance;
        _cellHeight = Fonts.Mono.GetLineHeight(FontSize);
        UpdateGeometry();

        _buffer = new TerminalBuffer();
        _shell = new Shell(this);
        _shell.Set("COLUMNS", _columns.ToString());
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
        if (_columnsChanged && !IsBusy)
        {
            _shell.Set("COLUMNS", _columns.ToString());
            _columnsChanged = false;
        }

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
            _selectionAnchor = null;   // the rows it referred to have moved
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

    public override (int Width, int Height) MinimumSize => (360, 200);

    protected override void OnResized()
    {
        int columns = _columns;
        UpdateGeometry();
        _columnsChanged |= columns != _columns;
        _scroll = 0;
        _selectionAnchor = null;
        _buffer.Changed = true;
    }

    /// <summary>Rows and columns of text that fit the current window size.</summary>
    private void UpdateGeometry()
    {
        _columns = Math.Max(20, (Bounds.W - 2 * Padding) / _cellWidth);
        _rows = Math.Max(3, (Bounds.H - TitleBarHeight - 2 * Padding) / _cellHeight);
    }

    public override void OnMouseDown(int x, int y)
    {
        var cell = CellAt(x, y);
        _selectionAnchor = cell;
        _selectionHead = cell;
        _buffer.Changed = true;
    }

    public override void OnMouseDrag(int x, int y)
    {
        if (_selectionAnchor is not null)
        {
            _selectionHead = CellAt(x, y);
            _buffer.Changed = true;
        }
    }

    private (int Row, int Column) CellAt(int x, int y)
    {
        int row = Math.Clamp((y - Padding) / _cellHeight, 0, Math.Max(0, _visibleRows.Count - 1));
        int column = Math.Clamp((x - Padding + _cellWidth / 2) / _cellWidth, 0, _columns);
        return (row, column);
    }

    private ((int Row, int Column) Start, (int Row, int Column) End)? SelectionRange
    {
        get
        {
            if (_selectionAnchor is not { } anchor || anchor == _selectionHead)
            {
                return null;
            }

            var head = _selectionHead;
            bool anchorFirst = anchor.Row < head.Row || (anchor.Row == head.Row && anchor.Column < head.Column);
            return anchorFirst ? (anchor, head) : (head, anchor);
        }
    }

    /// <summary>The selected text: wrapped rows join directly, real line ends become '\n'.</summary>
    private string SelectedText()
    {
        if (SelectionRange is not { } range)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (int row = range.Start.Row; row <= range.End.Row && row < _visibleRows.Count; row++)
        {
            var (cells, continues) = _visibleRows[row];
            int from = row == range.Start.Row ? range.Start.Column : 0;
            int to = row == range.End.Row ? range.End.Column : cells.Count;
            for (int c = from; c < Math.Min(to, cells.Count); c++)
            {
                text.Append(cells[c].Char);
            }

            if (row < range.End.Row && !continues)
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Types the clipboard into the input; each line break submits a line, as if typed.</summary>
    private void Paste()
    {
        string[] lines = Clipboard.Text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            _input.Insert(_cursor, lines[i]);
            _cursor += lines[i].Length;
            if (i < lines.Length - 1)
            {
                Submit();
            }
        }
    }

    public override void OnScroll(int delta)
    {
        _selectionAnchor = null;
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

        if (control && (key.Modifiers & ConsoleModifiers.Shift) != 0 && key.Key is ConsoleKeyEx.C or ConsoleKeyEx.V)
        {
            // Ctrl+Shift+C/V: the terminal clipboard keys (plain Ctrl+C interrupts).
            if (key.Key == ConsoleKeyEx.C)
            {
                string selected = SelectedText();
                if (selected.Length > 0)
                {
                    Clipboard.Text = selected;
                }
            }
            else
            {
                Paste();
            }

            _buffer.Changed = true;
            return;
        }

        _selectionAnchor = null;

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

        // What is shown: the scrollback, then its unfinished last line (e.g. a `read -p` prompt)
        // continued by the shell prompt and the input. Lines are wrapped here, at the current
        // width, so resizing the window reflows everything. While a command runs there is no
        // shell prompt, just whatever is being typed.
        string prompt = IsBusy ? string.Empty : CurrentPrompt;
        List<Cell> tail = _buffer.Line(_buffer.LineCount - 1);
        var inputLine = new TerminalBuffer();
        inputLine.AppendCells(tail);
        inputLine.Write(prompt + _input);
        List<Cell> input = inputLine.Line(0);

        int caretIndex = tail.Count + Ansi.Strip(prompt).Length + _cursor;
        int inputRows = Math.Max(TerminalBuffer.RowCount(input, _columns), caretIndex / _columns + 1);

        int total = inputRows;
        for (int i = 0; i < _buffer.LineCount - 1; i++)
        {
            total += TerminalBuffer.RowCount(_buffer.Line(i), _columns);
        }

        _scroll = Math.Min(_scroll, Math.Max(0, total - _rows));
        int end = total - _scroll;
        int start = Math.Max(0, end - _rows);
        int x0 = content.X + Padding, y0 = content.Y + Padding;

        // Collect the wrapped rows in [start, end), walking logical lines up from the bottom.
        var visible = new (List<Cell> Cells, bool Continues)[end - start];
        int rowIndex = total - inputRows;
        CollectRows(visible, TerminalBuffer.Wrap(input, _columns), rowIndex, start, end);
        for (int i = _buffer.LineCount - 2; i >= 0 && rowIndex > start; i--)
        {
            List<List<Cell>> rows = TerminalBuffer.Wrap(_buffer.Line(i), _columns);
            rowIndex -= rows.Count;
            CollectRows(visible, rows, rowIndex, start, end);
        }

        _visibleRows.Clear();
        _visibleRows.AddRange(visible);
        DrawSelection(surface, x0, y0);
        for (int r = 0; r < visible.Length; r++)
        {
            if (visible[r].Cells is not null)
            {
                DrawCells(surface, visible[r].Cells, x0, y0 + r * _cellHeight);
            }
        }

        if (IsFocused && _caretVisible && _scroll == 0)
        {
            int caretRow = total - inputRows + caretIndex / _columns - start;
            int caretCol = caretIndex % _columns;
            surface.FillRect(new Rect(x0 + caretCol * _cellWidth, y0 + caretRow * _cellHeight, 2, _cellHeight), Theme.Accent);
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

    /// <summary>Puts the rows of one logical line that lie in [start, end) into <paramref name="visible"/>.</summary>
    private static void CollectRows((List<Cell> Cells, bool Continues)[] visible, List<List<Cell>> rows, int firstRow, int start, int end)
    {
        for (int k = 0; k < rows.Count; k++)
        {
            int row = firstRow + k;
            if (row >= start && row < end)
            {
                visible[row - start] = (rows[k], k < rows.Count - 1);
            }
        }
    }

    private void DrawSelection(Surface surface, int x0, int y0)
    {
        if (SelectionRange is not { } range)
        {
            return;
        }

        for (int row = range.Start.Row; row <= range.End.Row && row < _visibleRows.Count; row++)
        {
            int from = row == range.Start.Row ? range.Start.Column : 0;
            int to = row == range.End.Row ? range.End.Column : _columns;
            if (to > from)
            {
                surface.FillRect(new Rect(x0 + from * _cellWidth, y0 + row * _cellHeight, (to - from) * _cellWidth, _cellHeight), SelectionColor);
            }
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
