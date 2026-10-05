using System.Collections.Generic;
using MyOS.Gui.Graphics;

namespace MyOS.Gui.Terminal;

/// <summary>One character cell: the character and its foreground color.</summary>
internal readonly struct Cell
{
    public Cell(char ch, uint color)
    {
        Char = ch;
        Color = color;
    }

    public char Char { get; }
    public uint Color { get; }
}

/// <summary>
/// The terminal's scrollback: lines of colored cells, hard-wrapped at a fixed column count.
/// Understands the escape sequences the shell emits: SGR colors (30-37, 90-97, 1 bold, 0 reset),
/// <c>ESC[2J</c> clear and <c>ESC[H</c> home.
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
    private int _baseColor = -1;

    public TerminalBuffer(int columns)
    {
        Columns = columns;
    }

    public int Columns { get; }
    public int LineCount => _lines.Count;

    /// <summary>Raised whenever the content changes, so the window can redraw.</summary>
    public bool Changed { get; set; }

    public List<Cell> Line(int index) => _lines[index];

    /// <summary>Length of the last (current) line, i.e. the column the next character lands in.</summary>
    public int CursorColumn => _lines[^1].Count;

    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new List<Cell>());
        Changed = true;
    }

    public void Write(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i = ParseEscape(text, i + 2);
                continue;
            }

            switch (c)
            {
                case '\n':
                    NewLine();
                    break;
                case '\r':
                    break;
                case '\t':
                    do
                    {
                        Put(' ');
                    }
                    while (CursorColumn % 8 != 0);
                    break;
                default:
                    Put(c);
                    break;
            }
        }

        Changed = true;
    }

    private void Put(char c)
    {
        if (_lines[^1].Count >= Columns)
        {
            NewLine();
        }

        _lines[^1].Add(new Cell(c, _color));
    }

    private void NewLine()
    {
        _lines.Add(new List<Cell>());
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }
    }

    /// <summary>Applies one CSI sequence starting after "ESC[" and returns the index of its final character.</summary>
    private int ParseEscape(string text, int i)
    {
        int start = i;
        while (i < text.Length && !char.IsLetter(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            return text.Length - 1;
        }

        string args = text.Substring(start, i - start);
        switch (text[i])
        {
            case 'm':
                foreach (string part in args.Length == 0 ? new[] { "0" } : args.Split(';'))
                {
                    ApplySgr(int.TryParse(part, out int code) ? code : 0);
                }

                break;
            case 'J':
                if (args == "2")
                {
                    Clear();
                }

                break;
        }

        return i;
    }

    private void ApplySgr(int code)
    {
        if (code == 0)
        {
            _bold = false;
            _baseColor = -1;
        }
        else if (code == 1)
        {
            _bold = true;
        }
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
