using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Shell;
using Zenith.Gui;
using Zenith.Gui.Graphics;
using Zenith.Gui.Terminal;

namespace Zenith.Apps;

/// <summary>
/// A terminal emulator window hosting a <see cref="Shell"/>. The scrollback is a
/// <see cref="TerminalBuffer"/>; the line being edited is kept separately and drawn after it.
/// Keys: arrows/Home/End edit, Up/Down history, Tab completes, Ctrl+C cancels, Ctrl+L clears,
/// Ctrl+U kills the line, Ctrl+D on an empty line exits; PageUp/PageDown or the wheel scroll.
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

    public TerminalWindow() : base("Terminal", 740, 470)
    {
        _cellWidth = Fonts.Mono.GetGlyph('M', FontSize).Advance;
        _cellHeight = Fonts.Mono.GetLineHeight(FontSize);
        int columns = (Bounds.W - 2 * Padding) / _cellWidth;
        _rows = (Bounds.H - TitleBarHeight - 2 * Padding) / _cellHeight;

        _buffer = new TerminalBuffer(columns);
        _shell = new Shell(this);
        _shell.Set("COLUMNS", columns.ToString());
        _shell.ExitRequested += Close;
        _shell.OpenApp = OpenAppFromShell;

        if (File.Exists("/etc/motd"))
        {
            _buffer.Write(File.ReadAllText("/etc/motd"));
        }

        _buffer.Write("\n");
    }

    /// <summary>Opens a desktop app by name; set once by the desktop.</summary>
    public static Func<string, bool>? OpenApp { get; set; }

    // IOutput: everything the shell and its commands print lands here.
    public void Write(string text) => _buffer.Write(text);

    public override bool Update()
    {
        bool visible = !IsFocused || Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 2) % 2 == 0;
        bool changed = visible != _caretVisible || _buffer.Changed;
        _caretVisible = visible;
        _buffer.Changed = false;
        return changed;
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
                _buffer.Write(_shell.Prompt + _input + Ansi.Dim("^C") + "\n");
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
                if (_input.Length == 0)
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
        _buffer.Write(_shell.Prompt + line + "\n");
        ResetInput();
        _shell.Execute(line);

        // Keep the prompt on its own line even if the command's output did not end in one.
        if (_buffer.CursorColumn != 0)
        {
            _buffer.Write("\n");
        }

        Title = "Terminal — " + _shell.WorkingDirectory;
    }

    private void ResetInput()
    {
        _input.Clear();
        _cursor = 0;
        _historyIndex = _shell.History.Count;
    }

    private void RecallHistory(int direction)
    {
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
            _buffer.Write(_shell.Prompt + _input + "\n" + string.Join("  ", candidates) + "\n");
        }
    }

    private bool OpenAppFromShell(string name) => OpenApp?.Invoke(name) ?? false;

    public override void DrawContent(Surface surface, Rect content)
    {
        surface.FillRect(content, Background);

        // The visible lines: the scrollback, then the prompt + input wrapped to the width.
        var promptBuffer = new TerminalBuffer(_buffer.Columns);
        promptBuffer.Write(_shell.Prompt + _input);
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
            int promptLength = Ansi.Strip(_shell.Prompt).Length;
            int position = promptLength + _cursor;
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
