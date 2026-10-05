using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Text;

/// <summary>
/// An editable plain-text document: lines plus a cursor, with the editing and movement
/// operations of a basic editor. UI-independent, so it is unit-tested on the host; the editor
/// window maps keys onto it and draws it.
/// </summary>
internal sealed class TextDocument
{
    private readonly List<string> _lines = new() { string.Empty };
    private int _preferredColumn;
    private (int Row, int Column)? _anchor;

    /// <summary>The file this document was loaded from or saved to; null for a new document.</summary>
    public string? Path { get; private set; }

    public bool Modified { get; private set; }
    public int Row { get; private set; }
    public int Column { get; private set; }
    public int LineCount => _lines.Count;

    public string Line(int index) => _lines[index];

    /// <summary>The whole text, lines joined by '\n'.</summary>
    public string Text => string.Join("\n", _lines);

    // --- selection: from the anchor to the cursor, in either direction ---

    public bool HasSelection => _anchor is not null && _anchor.Value != (Row, Column);

    /// <summary>The selection as (start, end) positions in document order; empty when nothing is selected.</summary>
    public ((int Row, int Column) Start, (int Row, int Column) End) Selection
    {
        get
        {
            var cursor = (Row, Column);
            if (!HasSelection)
            {
                return (cursor, cursor);
            }

            var anchor = _anchor!.Value;
            bool anchorFirst = anchor.Item1 < Row || (anchor.Item1 == Row && anchor.Item2 < Column);
            return anchorFirst ? (anchor, cursor) : (cursor, anchor);
        }
    }

    public string SelectedText
    {
        get
        {
            var (start, end) = Selection;
            if (start.Row == end.Row)
            {
                return _lines[start.Row].Substring(start.Column, end.Column - start.Column);
            }

            var text = new StringBuilder(_lines[start.Row].Substring(start.Column));
            for (int r = start.Row + 1; r < end.Row; r++)
            {
                text.Append('\n').Append(_lines[r]);
            }

            return text.Append('\n').Append(_lines[end.Row], 0, end.Column).ToString();
        }
    }

    /// <summary>Starts a selection at the cursor if none is active (call before a Shift+movement).</summary>
    public void BeginSelection()
    {
        _anchor ??= (Row, Column);
    }

    public void ClearSelection() => _anchor = null;

    public void SelectAll()
    {
        _anchor = (0, 0);
        MoveToEnd();
    }

    /// <summary>Removes the selected text; returns false when nothing was selected.</summary>
    public bool DeleteSelection()
    {
        if (!HasSelection)
        {
            _anchor = null;
            return false;
        }

        var (start, end) = Selection;
        string head = _lines[start.Row].Substring(0, start.Column);
        string tail = _lines[end.Row].Substring(end.Column);
        _lines.RemoveRange(start.Row + 1, end.Row - start.Row);
        _lines[start.Row] = head + tail;
        Row = start.Row;
        Column = start.Column;
        _anchor = null;
        Changed();
        return true;
    }

    public static TextDocument Open(string path)
    {
        var document = new TextDocument();
        if (File.Exists(path))
        {
            document.SetText(File.ReadAllText(path));
        }

        document.Path = path;
        document.Modified = false;
        return document;
    }

    public void SetText(string text)
    {
        _anchor = null;
        _lines.Clear();
        _lines.AddRange(text.Replace("\r\n", "\n").Split('\n'));
        Row = Column = _preferredColumn = 0;
        Modified = true;
    }

    /// <summary>Writes the document to <paramref name="path"/> (or its current path), ending with a newline like Unix tools expect.</summary>
    public void Save(string? path = null)
    {
        path ??= Path ?? throw new InvalidOperationException("no file name");
        string text = Text;
        if (!text.EndsWith('\n'))
        {
            text += "\n";
        }

        File.WriteAllText(path, text);
        Path = path;
        Modified = false;
    }

    // --- editing ---

    public void Insert(char c)
    {
        DeleteSelection();
        if (c == '\n')
        {
            NewLine();
            return;
        }

        string line = _lines[Row];
        _lines[Row] = line.Substring(0, Column) + c + line.Substring(Column);
        Column++;
        Changed();
    }

    /// <summary>Inserts text as-is (pasting): replaces the selection, no auto-indent on line breaks.</summary>
    public void Insert(string text)
    {
        DeleteSelection();
        foreach (char c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n')
            {
                BreakLine(indent: false);
            }
            else
            {
                Insert(c);
            }
        }
    }

    /// <summary>Splits the line at the cursor (Enter), carrying the current indentation over.</summary>
    public void NewLine()
    {
        DeleteSelection();
        BreakLine(indent: true);
    }

    private void BreakLine(bool indent)
    {
        string line = _lines[Row];
        string indentText = indent ? LeadingWhitespace(line, Column) : string.Empty;
        _lines[Row] = line.Substring(0, Column);
        _lines.Insert(Row + 1, indentText + line.Substring(Column));
        Row++;
        Column = indentText.Length;
        Changed();
    }

    public void Backspace()
    {
        if (DeleteSelection())
        {
            return;
        }

        if (Column > 0)
        {
            string line = _lines[Row];
            _lines[Row] = line.Remove(Column - 1, 1);
            Column--;
        }
        else if (Row > 0)
        {
            Column = _lines[Row - 1].Length;
            _lines[Row - 1] += _lines[Row];
            _lines.RemoveAt(Row);
            Row--;
        }
        else
        {
            return;
        }

        Changed();
    }

    public void Delete()
    {
        if (DeleteSelection())
        {
            return;
        }

        string line = _lines[Row];
        if (Column < line.Length)
        {
            _lines[Row] = line.Remove(Column, 1);
        }
        else if (Row < _lines.Count - 1)
        {
            _lines[Row] = line + _lines[Row + 1];
            _lines.RemoveAt(Row + 1);
        }
        else
        {
            return;
        }

        Changed();
    }

    // --- movement ---

    public void MoveLeft()
    {
        if (Column > 0)
        {
            Column--;
        }
        else if (Row > 0)
        {
            Row--;
            Column = _lines[Row].Length;
        }

        _preferredColumn = Column;
    }

    public void MoveRight()
    {
        if (Column < _lines[Row].Length)
        {
            Column++;
        }
        else if (Row < _lines.Count - 1)
        {
            Row++;
            Column = 0;
        }

        _preferredColumn = Column;
    }

    /// <summary>Moves by whole lines, keeping the column the cursor had before vertical movement started.</summary>
    public void MoveVertical(int lines)
    {
        Row = Math.Clamp(Row + lines, 0, _lines.Count - 1);
        Column = Math.Min(_preferredColumn, _lines[Row].Length);
    }

    public void MoveHome()
    {
        // First Home goes to the indentation, a second one to column 0.
        int indent = LeadingWhitespace(_lines[Row], _lines[Row].Length).Length;
        Column = Column == indent ? 0 : indent;
        _preferredColumn = Column;
    }

    public void MoveEnd()
    {
        Column = _lines[Row].Length;
        _preferredColumn = Column;
    }

    public void MoveToStart()
    {
        Row = Column = _preferredColumn = 0;
    }

    public void MoveToEnd()
    {
        Row = _lines.Count - 1;
        Column = _preferredColumn = _lines[Row].Length;
    }

    public void MoveTo(int row, int column)
    {
        Row = Math.Clamp(row, 0, _lines.Count - 1);
        Column = Math.Clamp(column, 0, _lines[Row].Length);
        _preferredColumn = Column;
    }

    private void Changed()
    {
        Modified = true;
        _preferredColumn = Column;
    }

    private static string LeadingWhitespace(string line, int limit)
    {
        var indent = new StringBuilder();
        for (int i = 0; i < limit && i < line.Length && (line[i] == ' ' || line[i] == '\t'); i++)
        {
            indent.Append(line[i]);
        }

        return indent.ToString();
    }
}
