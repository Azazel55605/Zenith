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
internal sealed class TerminalWindow : Window
{
    private const int FontSize = 14;
    private const int Padding = 10;

    private readonly TerminalSession _session;
    private readonly int _cellWidth;
    private readonly int _cellHeight;
    private int _rows;
    private int _columns;
    private int _scroll;   // lines scrolled back from the bottom
    private bool _caretVisible = true;
    private TerminalBuffer _buffer => _session.Buffer;

    // What the last frame showed: one entry per visible row, and whether that row's logical line
    // continues on the next row (a wrap, not a line break). Mouse selection works on these rows.
    private readonly List<(List<Cell> Cells, bool Continues)> _visibleRows = new();
    private (int Row, int Column)? _selectionAnchor;
    private (int Row, int Column) _selectionHead;

    public TerminalWindow() : base("Terminal", 740, 470)
    {
        _cellWidth = Fonts.Mono.GetGlyph('M', FontSize).Advance;
        _cellHeight = Fonts.Mono.GetLineHeight(FontSize);
        UpdateGeometry();

        _session = new TerminalSession(Identity, _columns, (name, argument) => OpenApp?.Invoke(name, argument) ?? false);
    }

    /// <summary>Opens a desktop app by name; set once by the desktop.</summary>
    public static Func<string, string?, bool>? OpenApp { get; set; }

    /// <summary>Whether a command line is running.</summary>
    public override bool AllowMaterials => !_session.SecretRead;
    public bool IsBusy => _session.IsBusy;
    public bool HasBackgroundJobs => _session.HasBackgroundJobs;
    public bool OwnsShell(Shell shell) => _session.OwnsShell(shell);
    public void PrepareSessionEnd() => _session.Cancel();
    public override void Close()
    {
        _session.Cancel();
        base.Close();
    }
    public override void EndSession()
    {
        _session.Cancel();
        Close();
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

    private void SyncWithJob()
    {
        _session.Sync(_columns);
        if (_session.TakeRowsMoved()) { _selectionAnchor = null; }   // the rows it referred to have moved
        if (_session.ExitRequested) { Close(); return; }
        if (_session.TakeJobFinished())
        {
            Identity = _session.Identity;
            Title = "Terminal — " + _session.WorkingDirectory;
        }
    }

    public override (int Width, int Height) MinimumSize => (360, 200);

    protected override void OnResized()
    {
        UpdateGeometry();
        _scroll = 0;
        _selectionAnchor = null;
        _buffer.Changed = true;
    }

    /// <summary>Rows and columns of text that fit the current window size.</summary>
    private void UpdateGeometry()
    {
        _columns = Math.Max(20, (Bounds.W - 2 * Padding) / _cellWidth);
        _rows = Math.Max(3, (Content.H - 2 * Padding) / _cellHeight);
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

    public override void OnScroll(int delta)
    {
        _selectionAnchor = null;
        _scroll = Math.Clamp(_scroll + delta * 3, 0, Math.Max(0, _buffer.LineCount - 1));
        _buffer.Changed = true;
    }

    public override void OnKey(KeyEvent key)
    {
        using var identity = CredentialContext.Enter(_session.Identity);
        _session.SyncNativeInput();
        if (_session.RawActive)
        {
            _session.KeyRaw(key);
            return;
        }
        if (_session.SecretRead && key.Key is ConsoleKeyEx.Tab or ConsoleKeyEx.UpArrow or ConsoleKeyEx.DownArrow) { return; }
        bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (key.Key != ConsoleKeyEx.PageUp && key.Key != ConsoleKeyEx.PageDown)
        {
            _scroll = 0;
        }

        if (control && (key.Modifiers & ConsoleModifiers.Shift) != 0 && key.Key is ConsoleKeyEx.C or ConsoleKeyEx.V)
        {
            if (_session.SecretRead) { return; }
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
                _session.Paste(Clipboard.Text);
            }

            _buffer.Changed = true;
            return;
        }

        _selectionAnchor = null;
        if (!control && key.Key == ConsoleKeyEx.PageUp) { OnScroll(_rows / 3); return; }
        if (!control && key.Key == ConsoleKeyEx.PageDown) { OnScroll(-_rows / 3); return; }
        _session.Key(key);
    }

    public override bool AllowBackgroundMaterials => true;

    public override void DrawContent(Surface surface, Rect content)
    {
        PaintWorkspace(surface, content, 0, 4);

        // What is shown: the scrollback, then its unfinished last line (e.g. a `read -p` prompt)
        // continued by the shell prompt and the input. Lines are wrapped here, at the current
        // width, so resizing the window reflows everything. While a command runs there is no
        // shell prompt, just whatever is being typed.
        string prompt = _session.DisplayPrompt;
        List<Cell> tail = _buffer.Line(_buffer.LineCount - 1);
        var inputLine = new TerminalBuffer();
        inputLine.AppendCells(tail);
        inputLine.Write(prompt + (_session.SecretRead ? new string('*', _session.InputLength) : _session.DisplayInput));
        List<Cell> input = inputLine.Line(0);

        int caretIndex = tail.Count + Ansi.Strip(prompt).Length + _session.Cursor;
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
        bool light = Theme.IsLight;
        for (int r = 0; r < visible.Length; r++)
        {
            if (visible[r].Cells is not null)
            {
                DrawCells(surface, visible[r].Cells, x0, y0 + r * _cellHeight, light);
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
                surface.FillRect(new Rect(x0 + from * _cellWidth, y0 + row * _cellHeight, (to - from) * _cellWidth, _cellHeight), Theme.AccentSoft);
            }
        }
    }

    /// <summary>Draws a row of cells, batching runs of the same color into one string.</summary>
    private void DrawCells(Surface surface, List<Cell> cells, int x, int y, bool light)
    {
        int start = 0;
        while (start < cells.Count)
        {
            uint color = cells[start].ResolveColor(Theme.TextPrimary, light);
            int end = start;
            var run = new StringBuilder();
            while (end < cells.Count && cells[end].ResolveColor(Theme.TextPrimary, light) == color)
            {
                run.Append(cells[end].Char);
                end++;
            }

            surface.DrawText(run.ToString(), Fonts.Mono, FontSize, color, x + start * _cellWidth, y);
            start = end;
        }
    }
}
