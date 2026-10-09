using System;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core;
using Zenith.Core.Files;
using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

/// <summary>Small file browser: explicit actions, keyboard navigation and visible errors.</summary>
internal sealed class FileManagerWindow : Window
{
    public static Func<string, string?, bool>? OpenApp;
    private readonly FileBrowser _browser = new();
    private int _selected = -1, _top;
    private string _message = "Select an item, then Open. F5 refresh · Ctrl+L location";
    private string? _transfer;
    private bool _move;
    private enum Prompt { None, Location, File, Folder, Rename, Paste, Delete }
    private Prompt _prompt;
    private readonly StringBuilder _input = new();
    private string? _subject;
    private bool _dirty = true;
    private const int ListY = 116, RowHeight = 28, StatusHeight = 28;
    private static readonly string[] Buttons = { "Up", "Refresh", "New file", "Folder", "Open", "Rename", "Copy", "Move", "Paste", "Delete" };

    public FileManagerWindow(string? path = null) : base("Files", 760, 480)
    {
        Run(() => _browser.Navigate(path ?? "/home/user"));
    }

    public override (int Width, int Height) MinimumSize => (720, 320);
    public override bool Update() { bool dirty = _dirty; _dirty = false; return dirty; }
    private FileEntry? Selection => _selected >= 0 && _selected < _browser.Entries.Count ? _browser.Entries[_selected] : null;
    private int Rows => Math.Max(1, (Content.H - ListY - StatusHeight) / RowHeight);

    private void Run(Action action, string? success = null, string? select = null)
    {
        try
        {
            action();
            if (select is not null)
                _selected = _browser.Entries.FindIndex(item => item.Path == select);
            _selected = Math.Clamp(_selected, _browser.Entries.Count == 0 ? -1 : 0, _browser.Entries.Count - 1);
            _top = Math.Clamp(_top, 0, Math.Max(0, _browser.Entries.Count - Rows));
            if (success is not null)
            {
                _message = success;
                Log.Write("files", success);
            }
            Title = "Files - " + _browser.CurrentPath;
        }
        catch (Exception e) { _message = "Cannot complete: " + e.Message; Log.Write("files", "error: " + e.Message); }
        _dirty = true;
    }

    private void Start(Prompt prompt, string initial = "")
    {
        _prompt = prompt; _input.Clear().Append(initial); _subject = Selection?.Path; _dirty = true;
    }

    private void Action(int index)
    {
        if (_prompt != Prompt.None)
        {
            return;
        }
        switch (index)
        {
            case 0: Run(_browser.Up); _top = 0; break;
            case 1: Run(_browser.Refresh); break;
            case 2: Start(Prompt.File); break;
            case 3: Start(Prompt.Folder); break;
            case 4:
                if (Selection is not FileEntry entry)
                {
                    return;
                }
                if (entry.Directory) { Run(() => _browser.Navigate(entry.Path)); _selected = _browser.Entries.Count == 0 ? -1 : 0; _top = 0; }
                else Run(() => { FileBrowser.RequireEditable(entry.Path); if (OpenApp?.Invoke("Editor", entry.Path) != true) throw new InvalidOperationException("Editor is unavailable."); });
                break;
            case 5: if (Selection is not null) Start(Prompt.Rename, Selection.Name); break;
            case 6:
            case 7:
                if (Selection is null)
                {
                    return;
                }
                _transfer = Selection.Path; _move = index == 7;
                _message = (_move ? "Move: " : "Copy: ") + Selection.Name + " - browse to destination, then Paste";
                break;
            case 8: if (_transfer is not null) Start(Prompt.Paste, System.IO.Path.GetFileName(_transfer)); break;
            case 9: if (Selection is not null) Start(Prompt.Delete); break;
        }
        _dirty = true;
    }

    private void Submit()
    {
        Prompt prompt = _prompt;
        _prompt = Prompt.None;
        string name = _input.ToString();
        Run(() =>
        {
            string? selected = null;
            switch (prompt)
            {
                case Prompt.Location: _browser.Navigate(name); _selected = -1; _top = 0; break;
                case Prompt.File:
                case Prompt.Folder: selected = _browser.Create(name, prompt == Prompt.Folder); _message = "created " + selected; break;
                case Prompt.Rename: selected = _browser.Transfer(_subject!, name, true); _message = "renamed " + selected; break;
                case Prompt.Paste:
                    selected = _browser.Transfer(_transfer!, name, _move);
                    _message = (_move ? "moved " : "copied ") + selected;
                    if (_move)
                    {
                        _transfer = null;
                    }
                    break;
                case Prompt.Delete: _browser.Delete(_subject!); _message = "deleted " + _subject; break;
            }
            if (selected is not null)
            {
                _selected = _browser.Entries.FindIndex(item => item.Path == selected);
            }
            if (prompt != Prompt.Location)
            {
                Log.Write("files", _message);
            }
        });
    }

    public override void OnKey(KeyEvent key)
    {
        bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        _dirty = true;
        if (_prompt != Prompt.None)
        {
            if (key.Key == ConsoleKeyEx.Escape) { _prompt = Prompt.None; _message = "Cancelled"; }
            else if (key.Key == ConsoleKeyEx.Enter) Submit();
            else if (control && key.Key == ConsoleKeyEx.A) _input.Clear();
            else if (key.Key == ConsoleKeyEx.Backspace && _input.Length > 0) _input.Length--;
            else if (_prompt != Prompt.Delete && key.KeyChar >= ' ') _input.Append(key.KeyChar);
            return;
        }
        if (control)
        {
            switch (key.Key)
            {
                case ConsoleKeyEx.L: Start(Prompt.Location, _browser.CurrentPath); break;
                case ConsoleKeyEx.N: Action(shift ? 3 : 2); break;
                case ConsoleKeyEx.C: Action(6); break;
                case ConsoleKeyEx.X: Action(7); break;
                case ConsoleKeyEx.V: Action(8); break;
                case ConsoleKeyEx.D: Action(9); break;
                case ConsoleKeyEx.Q: Close(); break;
            }
            return;
        }
        switch (key.Key)
        {
            case ConsoleKeyEx.UpArrow: Select(_selected - 1); break;
            case ConsoleKeyEx.DownArrow: Select(_selected + 1); break;
            case ConsoleKeyEx.Home: Select(0); break;
            case ConsoleKeyEx.End: Select(_browser.Entries.Count - 1); break;
            case ConsoleKeyEx.PageUp: Select(_selected - Rows); break;
            case ConsoleKeyEx.PageDown: Select(_selected + Rows); break;
            case ConsoleKeyEx.Enter: Action(4); break;
            case ConsoleKeyEx.Backspace: Action(0); break;
            case ConsoleKeyEx.F2: Action(5); break;
            case ConsoleKeyEx.F5: Action(1); break;
            case ConsoleKeyEx.Delete: Action(9); break;
        }
    }

    private void Select(int index)
    {
        _selected = _browser.Entries.Count == 0 ? -1 : Math.Clamp(index, 0, _browser.Entries.Count - 1);
        if (_selected < _top)
        {
            _top = Math.Max(0, _selected);
        }
        if (_selected >= _top + Rows)
        {
            _top = _selected - Rows + 1;
        }
    }

    public override void OnScroll(int delta) { _top = Math.Clamp(_top - delta * 3, 0, Math.Max(0, _browser.Entries.Count - Rows)); _dirty = true; }
    public override void OnMouseDown(int x, int y)
    {
        if (_prompt != Prompt.None)
        {
            if (y >= Content.H - 78 && y < Content.H - 46)
            {
                if (x >= Content.W - 190 && x < Content.W - 105)
                {
                    Submit();
                }
                else if (x >= Content.W - 100) { _prompt = Prompt.None; _dirty = true; }
            }
            return;
        }
        if (y < 40 && x >= 12) { Start(Prompt.Location, _browser.CurrentPath); return; }
        if (y >= 44 && y < 76 && x >= 12)
        {
            int index = (x - 12) / 69;
            if (index < Buttons.Length)
            {
                Action(index);
            }
            return;
        }
        if (y >= ListY && y < Content.H - StatusHeight)
        {
            int index = _top + (y - ListY) / RowHeight;
            if (index < _browser.Entries.Count)
            {
                Select(index);
            }
        }
    }

    public override void DrawContent(Surface surface, Rect content)
    {
        surface.FillRect(content, Theme.WindowBackground);
        Text(surface, content, _browser.CurrentPath + "  (click to change)", 12, 14, content.W - 24, Theme.TextPrimary);
        for (int i = 0; i < Buttons.Length; i++)
            Button(surface, new Rect(content.X + 12 + i * 69, content.Y + 44, 65, 32), Buttons[i]);
        Text(surface, content, "Name", 14, 88, content.W - 155, Theme.TextSecondary);
        Text(surface, content, "Type / bytes", content.W - 140, 88, 126, Theme.TextSecondary);
        for (int row = 0; row < Rows && _top + row < _browser.Entries.Count; row++)
        {
            int index = _top + row;
            FileEntry entry = _browser.Entries[index];
            int y = ListY + row * RowHeight;
            if (index == _selected)
            {
                surface.FillRect(new Rect(content.X + 8, content.Y + y, content.W - 16, RowHeight), Theme.AccentSoft);
            }
            Text(surface, content, entry.Name, 14, y + 6, content.W - 165, Theme.TextPrimary);
            Text(surface, content, entry.Directory ? "Folder" : entry.Size?.ToString() ?? "File", content.W - 140, y + 6, 126, Theme.TextSecondary);
        }
        if (_browser.Entries.Count == 0)
        {
            Text(surface, content, "This folder is empty.", 14, ListY + 14, content.W - 28, Theme.TextMuted);
        }
        surface.FillRect(new Rect(content.X, content.Bottom - StatusHeight, content.W, StatusHeight), Theme.Panel);
        Text(surface, content, _message, 12, content.H - 21, content.W - 24, Theme.TextSecondary);
        if (_prompt != Prompt.None)
        {
            var panel = new Rect(content.X + 8, content.Bottom - 140, content.W - 16, 104);
            surface.FillRoundRect(panel, 6, Theme.Menu);
            string label = _prompt switch { Prompt.Location => "Location", Prompt.File => "New file name", Prompt.Folder => "New folder name", Prompt.Rename => "Rename to", Prompt.Paste => "Destination name", _ => "Delete " + System.IO.Path.GetFileName(_subject) + "? (empty folders only)" };
            Text(surface, content, label, 20, content.H - 130, content.W - 40, Theme.TextPrimary);
            if (_prompt != Prompt.Delete)
            {
                Text(surface, content, _input + "_", 20, content.H - 103, content.W - 40, Theme.Accent);
            }
            Button(surface, new Rect(content.Right - 190, content.Bottom - 78, 85, 32), _prompt == Prompt.Delete ? "Delete" : "OK");
            Button(surface, new Rect(content.Right - 100, content.Bottom - 78, 80, 32), "Cancel");
        }
    }

    private static void Button(Surface surface, Rect rect, string label)
    {
        surface.FillRoundRect(rect, 5, Theme.Hover);
        surface.DrawTextCentered(label, Fonts.Regular, Theme.TextSmall, Theme.TextPrimary, rect, true);
    }
    private static void Text(Surface surface, Rect content, string text, int x, int y, int width, uint color)
    {
        Rect clip = surface.Clip;
        surface.Clip = new Rect(content.X + x, content.Y + y, Math.Max(0, width), 22).Intersect(content);
        surface.DrawText(text, Fonts.Regular, Theme.TextBody, color, content.X + x, content.Y + y);
        surface.Clip = clip;
    }
}
