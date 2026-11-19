using System;
using System.Collections.Generic;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Terminal;

/// <summary>One character cell: the character and its foreground color.</summary>
internal readonly struct Cell
{
    public Cell(char ch, uint color) : this(ch, color, -2) { }
    internal Cell(char ch, uint color, int style, bool inverse = false)
    {
        Char = ch;
        Color = color;
        _style = style;
        Inverse = inverse;
    }
    private readonly int _style;
    public uint ResolveColor(uint foreground, bool light)
        => _style == -2 ? Color : _style < 0 ? foreground : TerminalColors.Resolve(_style, light);

    public bool Inverse { get; }
    public char Char { get; }
    public uint Color { get; }
}

/// <summary>
/// The terminal's scrollback: logical lines of colored cells. Lines are not wrapped here;
/// <see cref="Wrap"/> splits them into rows for whatever width the window has right now, so
/// the text reflows when the window is resized.
/// Understands the escape sequences the shell emits: SGR colors (30-37, 90-97, 1 bold, 0 reset),
/// <c>ESC[2J</c> clear, carriage-return overwrite and <c>ESC[2K</c> erase-line.
/// </summary>
internal sealed class TerminalBuffer
{
    private const int MaxLines = 2000;

    // ANSI colors 0-7 and their bright variants, tuned for the dark theme.
    private static readonly uint[] s_palette =
    {
        0xFF4A5068, 0xFFF0707A, 0xFF8BD49C, 0xFFF2CB74, 0xFF7C9CFF, 0xFFC792EA, 0xFF6FD3E3, 0xFFD8DCE8,
        0xFF6B7287, 0xFFFF8A93, 0xFFA6E8B5, 0xFFFFDC8F, 0xFF9DB6FF, 0xFFDDB0F5, 0xFF8FE6F2, 0xFFFFFFFF,
    };

    private readonly List<List<Cell>> _lines = new() { new List<Cell>() };
    private uint _color = Theme.TextPrimary;
    private bool _bold;
    private int _cursor;
    private int _baseColor = -1;

    private bool _inverse;
    private bool _positioned;
    private int _row;
    private string? _escape;
    public TerminalBuffer? Alternate { get; private set; }
    public int CaretRow { get; private set; }
    public int CaretColumn { get; private set; }
    internal void EndAlternate()
    {
        if (Alternate is null) { return; }
        Alternate = null;
        Changed = true;
    }

    public int LineCount => _lines.Count;

    /// <summary>Raised whenever the content changes, so the window can redraw.</summary>
    public bool Changed { get; set; }

    public List<Cell> Line(int index) => _lines[index];

    /// <summary>The write column in the last logical line.</summary>
    public int CursorColumn => _cursor;

    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new List<Cell>());
        _row = _cursor = CaretRow = CaretColumn = 0;
        Changed = true;
    }

    public void Write(string text)
    {
        foreach (char c in text)
        {
            if (c == '\u001b') { _escape = ""; continue; }
            if (_escape is string sequence)
            {
                if (sequence.Length == 0)
                {
                    _escape = c == '[' ? "[" : null;
                    if (c == '[') { continue; }
                }
                else
                {
                    if (c is >= '@' and <= '~')
                    {
                        string args = sequence[1..];
                        if (args == "?1049" && c == 'h') { Alternate ??= new TerminalBuffer { _positioned = true }; }
                        else if (args == "?1049" && c == 'l') { EndAlternate(); }
                        else { (Alternate ?? this).ApplyEscape(args, c); }
                        _escape = null;
                    }
                    else { _escape = sequence.Length < 64 ? sequence + c : null; }
                    continue;
                }
            }
            (Alternate ?? this).WriteCharacter(c);
        }

        Changed = true;
    }

    private void WriteCharacter(char c)
    {
        switch (c)
        {
            case '\b': _cursor = Math.Max(0, _cursor - 1); break;
            case '\n': NewLine(); break;
            case '\r': _cursor = 0; break;
            case '\t':
                do { Put(' '); } while (CursorColumn % 8 != 0);
                break;
            default: Put(c); break;
        }
    }

    /// <summary>Appends already-colored cells (e.g. a copy of another buffer's unfinished line).</summary>
    public void AppendCells(List<Cell> cells)
    {
        foreach (Cell cell in cells)
        {
            Put(cell);
        }

        Changed = true;
    }

    private int Style => _baseColor < 0 ? -1 : _bold && _baseColor < 8 ? _baseColor + 8 : _baseColor;
    private void Put(char c) => Put(new Cell(c, _color, Style, _inverse));

    private List<Cell> ActiveLine()
    {
        if (!_positioned) { return _lines[^1]; }
        while (_lines.Count <= _row) { _lines.Add(new List<Cell>()); }
        return _lines[_row];
    }

    private void Put(Cell cell)
    {
        if (_positioned && _cursor >= 512) { return; }
        var line = ActiveLine();
        while (line.Count < _cursor) { line.Add(new Cell(' ', _color, Style)); }
        if (_cursor < line.Count) { line[_cursor] = cell; } else { line.Add(cell); }
        _cursor++;
    }

    /// <summary>Splits a logical line into rows of at most <paramref name="columns"/> cells (an empty line is one empty row).</summary>
    public static List<List<Cell>> Wrap(List<Cell> line, int columns)
    {
        var rows = new List<List<Cell>>();
        for (int start = 0; start < line.Count; start += columns)
        {
            rows.Add(line.GetRange(start, Math.Min(columns, line.Count - start)));
        }

        if (rows.Count == 0)
        {
            rows.Add(new List<Cell>());
        }

        return rows;
    }

    /// <summary>How many rows a logical line takes at <paramref name="columns"/> wide.</summary>
    public static int RowCount(List<Cell> line, int columns) => Math.Max(1, (line.Count + columns - 1) / columns);

    private void NewLine()
    {
        _cursor = 0;
        if (_positioned) { _row = Math.Min(511, _row + 1); return; }
        _lines.Add(new List<Cell>());
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }
    }

    private void ApplyEscape(string args, char final)
    {
        switch (final)
        {
            case 'm':
                foreach (string part in args.Length == 0 ? new[] { "0" } : args.Split(';'))
                {
                    if (int.TryParse(part, out int code)) { ApplySgr(code); }
                }
                break;
            case 'H':
                string[] position = args.Split(';');
                CaretRow = Coordinate(position[0]);
                CaretColumn = position.Length > 1 ? Coordinate(position[1]) : 0;
                if (_positioned) { _row = CaretRow; _cursor = CaretColumn; }
                break;
            case 'K':
                List<Cell> line = ActiveLine();
                if (args == "2") { line.Clear(); }
                else if (args is "" or "0" && _cursor < line.Count)
                { line.RemoveRange(_cursor, line.Count - _cursor); }
                break;
            case 'J':
                if (args == "2") { Clear(); }
                break;
        }
    }

    private static int Coordinate(string value)
        => int.TryParse(value, out int coordinate) ? Math.Clamp(coordinate, 1, 512) - 1 : 0;

    private void ApplySgr(int code)
    {
        if (code == 0)
        {
            _bold = _inverse = false;
            _baseColor = -1;
        }
        else if (code == 1)
        {
            _bold = true;
        }
        else if (code == 7) { _inverse = true; }
        else if (code == 27) { _inverse = false; }
        else if (code >= 30 && code <= 37)
        {
            _baseColor = code - 30;
        }
        else if (code >= 90 && code <= 97)
        {
            _baseColor = code - 90 + 8;
        }
        else if (code == 39)
        {
            _baseColor = -1;
        }

        // No bold font face: bold renders as the bright color instead, like classic terminals.
        _color = _baseColor < 0 ? (_bold ? 0xFFFFFFFF : Theme.TextPrimary)
            : s_palette[_bold && _baseColor < 8 ? _baseColor + 8 : _baseColor];
    }
}
