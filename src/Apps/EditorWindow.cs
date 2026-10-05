using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Text;
using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

/// <summary>
/// A plain-text editor for files (opened with <c>edit file</c> from the terminal, or from the
/// launcher). The editing model is <see cref="TextDocument"/>; this window maps keys and draws.
/// Ctrl+S save, Ctrl+O open, Ctrl+N new, Ctrl+Q close; Tab inserts four spaces.
/// </summary>
internal sealed class EditorWindow : Window
{
    private const int FontSize = 14;
    private const int Padding = 8;
    private const int StatusHeight = 26;
    private const string HomeDirectory = "/home/user";

    private const uint Background = 0xFF161A26;
    private const uint GutterText = 0xFF4D556B;
    private const uint CurrentLine = 0x0AFFFFFF;
    private const uint StatusBar = 0xFF1F2433;

    private enum Prompt
    {
        None,
        SaveAs,
        Open,
    }

    private readonly int _cellWidth;
    private readonly int _lineHeight;
    private TextDocument _document;
    private int _top;        // first visible line
    private int _left;       // first visible column
    private int _rows = 1;
    private int _columns = 1;
    private string _message = "Ctrl+S save · Ctrl+O open · Ctrl+N new · Ctrl+Q close";
    private bool _confirmDiscard;
    private Prompt _prompt;
    private readonly StringBuilder _promptInput = new();
    private bool _caretVisible = true;
    private bool _dirty = true;

    public EditorWindow(string? path = null) : base("Editor", 720, 480)
    {
        _cellWidth = Fonts.Mono.GetGlyph('M', FontSize).Advance;
        _lineHeight = Fonts.Mono.GetLineHeight(FontSize) + 2;
        _document = path is null ? new TextDocument() : TextDocument.Open(path);
        if (path is not null && !File.Exists(path))
        {
            _message = "New file";
        }

        UpdateTitle();
    }

    public override bool Update()
    {
        bool visible = !IsFocused || Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 2) % 2 == 0;
        bool changed = visible != _caretVisible || _dirty;
        _caretVisible = visible;
        _dirty = false;
        return changed;
    }

    public override void OnScroll(int delta)
    {
        _top = Math.Clamp(_top - delta * 3, 0, Math.Max(0, _document.LineCount - 1));
        _dirty = true;
    }

    public override void OnMouseDown(int x, int y)
    {
        int row = _top + (y - Padding) / _lineHeight;
        int column = _left + Math.Max(0, (x - TextX(0) + _cellWidth / 2) / _cellWidth);
        _document.MoveTo(row, column);
        _dirty = true;
    }

    public override void OnKey(KeyEvent key)
    {
        _dirty = true;
        if (_prompt != Prompt.None)
        {
            HandlePromptKey(key);
            return;
        }

        bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (control)
        {
            HandleCommand(key.Key);
            return;
        }

        _confirmDiscard = false;
        switch (key.Key)
        {
            case ConsoleKeyEx.Enter:
                _document.NewLine();
                break;
            case ConsoleKeyEx.Backspace:
                _document.Backspace();
                break;
            case ConsoleKeyEx.Delete:
                _document.Delete();
                break;
            case ConsoleKeyEx.Tab:
                _document.Insert("    ");
                break;
            case ConsoleKeyEx.LeftArrow:
                _document.MoveLeft();
                break;
            case ConsoleKeyEx.RightArrow:
                _document.MoveRight();
                break;
            case ConsoleKeyEx.UpArrow:
                _document.MoveVertical(-1);
                break;
            case ConsoleKeyEx.DownArrow:
                _document.MoveVertical(1);
                break;
            case ConsoleKeyEx.PageUp:
                _document.MoveVertical(-_rows);
                break;
            case ConsoleKeyEx.PageDown:
                _document.MoveVertical(_rows);
                break;
            case ConsoleKeyEx.Home:
                _document.MoveHome();
                break;
            case ConsoleKeyEx.End:
                _document.MoveEnd();
                break;
            default:
                if (key.KeyChar >= ' ')
                {
                    _document.Insert(key.KeyChar);
                }

                break;
        }

        UpdateTitle();
        ScrollToCursor();
    }

    private void HandleCommand(ConsoleKeyEx key)
    {
        switch (key)
        {
            case ConsoleKeyEx.S:
                if (_document.Path is null)
                {
                    StartPrompt(Prompt.SaveAs, string.Empty);
                }
                else
                {
                    Save(_document.Path);
                }

                break;
            case ConsoleKeyEx.O:
                if (GuardUnsaved("open another file"))
                {
                    StartPrompt(Prompt.Open, string.Empty);
                }

                break;
            case ConsoleKeyEx.N:
                if (GuardUnsaved("start a new file"))
                {
                    _document = new TextDocument();
                    _top = _left = 0;
                    _message = "New file";
                    UpdateTitle();
                }

                break;
            case ConsoleKeyEx.Q:
                if (GuardUnsaved("close"))
                {
                    Close();
                }

                break;
            case ConsoleKeyEx.Home:
                _document.MoveToStart();
                ScrollToCursor();
                break;
            case ConsoleKeyEx.End:
                _document.MoveToEnd();
                ScrollToCursor();
                break;
        }
    }

    /// <summary>With unsaved changes, the first attempt only warns; repeating the same keys goes ahead.</summary>
    private bool GuardUnsaved(string action)
    {
        if (!_document.Modified || _confirmDiscard)
        {
            _confirmDiscard = false;
            return true;
        }

        _confirmDiscard = true;
        _message = "Unsaved changes. Press again to " + action + " anyway, or Ctrl+S to save.";
        return false;
    }

    private void StartPrompt(Prompt prompt, string initial)
    {
        _prompt = prompt;
        _promptInput.Clear().Append(initial);
    }

    private void HandlePromptKey(KeyEvent key)
    {
        switch (key.Key)
        {
            case ConsoleKeyEx.Escape:
                _prompt = Prompt.None;
                _message = "Cancelled";
                return;
            case ConsoleKeyEx.Backspace:
                if (_promptInput.Length > 0)
                {
                    _promptInput.Length--;
                }

                return;
            case ConsoleKeyEx.Enter:
                string path = ResolvePath(_promptInput.ToString().Trim());
                Prompt prompt = _prompt;
                _prompt = Prompt.None;
                if (path.Length == 0)
                {
                    return;
                }

                if (prompt == Prompt.SaveAs)
                {
                    Save(path);
                }
                else
                {
                    OpenFile(path);
                }

                return;
            default:
                if (key.KeyChar >= ' ')
                {
                    _promptInput.Append(key.KeyChar);
                }

                return;
        }
    }

    private void Save(string path)
    {
        try
        {
            _document.Save(path);
            _message = "Saved " + path;
            _confirmDiscard = false;
        }
        catch (Exception e)
        {
            _message = "Cannot save: " + e.Message;
        }

        UpdateTitle();
    }

    private void OpenFile(string path)
    {
        if (Directory.Exists(path))
        {
            _message = path + " is a directory";
            return;
        }

        try
        {
            _document = TextDocument.Open(path);
            _top = _left = 0;
            _message = File.Exists(path) ? "Opened " + path : "New file " + path;
        }
        catch (Exception e)
        {
            _message = "Cannot open: " + e.Message;
        }

        UpdateTitle();
    }

    private static string ResolvePath(string input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        if (input.StartsWith("~/"))
        {
            input = HomeDirectory + input.Substring(1);
        }

        return Path.GetFullPath(input.StartsWith('/') ? input : HomeDirectory + "/" + input);
    }

    private void UpdateTitle()
    {
        string name = _document.Path is null ? "untitled" : Path.GetFileName(_document.Path);
        Title = "Editor — " + name + (_document.Modified ? " •" : string.Empty);
    }

    private void ScrollToCursor()
    {
        if (_document.Row < _top)
        {
            _top = _document.Row;
        }
        else if (_document.Row >= _top + _rows)
        {
            _top = _document.Row - _rows + 1;
        }

        if (_document.Column < _left)
        {
            _left = _document.Column;
        }
        else if (_document.Column >= _left + _columns)
        {
            _left = _document.Column - _columns + 1;
        }
    }

    private int GutterWidth => (Math.Max(3, _document.LineCount.ToString().Length) + 2) * _cellWidth;

    private int TextX(int contentX) => contentX + Padding + GutterWidth;

    public override void DrawContent(Surface surface, Rect content)
    {
        surface.FillRect(content, Background);

        Rect text = new(content.X, content.Y, content.W, content.H - StatusHeight);
        _rows = Math.Max(1, (text.H - Padding) / _lineHeight);
        _columns = Math.Max(1, (text.Right - TextX(text.X) - Padding) / _cellWidth);

        int gutter = GutterWidth;
        for (int i = 0; i < _rows && _top + i < _document.LineCount; i++)
        {
            int row = _top + i;
            int y = text.Y + Padding + i * _lineHeight;
            if (row == _document.Row)
            {
                surface.FillRect(new Rect(text.X, y - 1, text.W, _lineHeight), CurrentLine);
            }

            string number = (row + 1).ToString();
            surface.DrawText(number, Fonts.Mono, FontSize, row == _document.Row ? Theme.TextSecondary : GutterText,
                text.X + Padding + gutter - (number.Length + 1) * _cellWidth, y);

            string line = _document.Line(row);
            if (line.Length > _left)
            {
                string visible = line.Substring(_left, Math.Min(_columns, line.Length - _left));
                surface.DrawText(visible, Fonts.Mono, FontSize, Theme.TextPrimary, TextX(text.X), y);
            }
        }

        if (IsFocused && _caretVisible && _prompt == Prompt.None)
        {
            int cx = TextX(text.X) + (_document.Column - _left) * _cellWidth;
            int cy = text.Y + Padding + (_document.Row - _top) * _lineHeight;
            if (_document.Row >= _top && _document.Row < _top + _rows)
            {
                surface.FillRect(new Rect(cx, cy, 2, _lineHeight - 2), Theme.Accent);
            }
        }

        DrawStatusBar(surface, new Rect(content.X, text.Bottom, content.W, StatusHeight));
    }

    private void DrawStatusBar(Surface surface, Rect bar)
    {
        surface.FillRect(bar, StatusBar);
        surface.FillRect(new Rect(bar.X, bar.Y, bar.W, 1), Theme.TitleDivider);

        if (_prompt != Prompt.None)
        {
            string label = (_prompt == Prompt.SaveAs ? "Save as: " : "Open: ") + _promptInput;
            surface.DrawTextCentered(label, Fonts.Mono, 13, Theme.TextPrimary, new Rect(bar.X + 12, bar.Y, bar.W - 24, bar.H), false);
            int caretX = bar.X + 12 + Fonts.Mono.MeasureString(label, 13);
            if (_caretVisible)
            {
                surface.FillRect(new Rect(caretX, bar.Y + 6, 2, bar.H - 12), Theme.Accent);
            }

            return;
        }

        string position = "Ln " + (_document.Row + 1) + ", Col " + (_document.Column + 1);
        int positionWidth = Fonts.Regular.MeasureString(position, Theme.TextSmall);
        surface.DrawTextCentered(position, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary,
            new Rect(bar.Right - positionWidth - 12, bar.Y, positionWidth, bar.H), false);

        surface.Clip = new Rect(bar.X + 12, bar.Y, bar.W - positionWidth - 36, bar.H);
        surface.DrawTextCentered(_message, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, surface.Clip, false);
        surface.Clip = new Rect(bar.X + 1, bar.Y, bar.W - 2, bar.H);
    }
}
