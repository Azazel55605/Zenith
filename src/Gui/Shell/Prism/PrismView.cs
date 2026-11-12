// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell.Prism;

/// <summary>
/// Prism (docs/design/prism-concept.svg): one search over installed apps, open windows, settings
/// pages, actions, a calculator and commands, shown either as the start menu above the taskbar
/// button or as a palette in the upper third of the screen (Meta or Alt+Space). While open it owns
/// the keyboard: type to search, Up/Down choose, Enter opens, Tab narrows to one kind, Esc closes.
/// </summary>
internal sealed class PrismView
{
    public enum Layout { Menu, Palette }
    private const int Row = 46, Header = 26, Pad = 12;
    private readonly Func<List<PrismItem>> _candidates;
    private readonly Action<PrismItem> _activate;
    private readonly Func<string> _user;
    private List<PrismItem> _all = new(), _results = new();
    private string _query = "";
    private int _filter, _selected, _scroll, _maxHeight = 560;
    private readonly List<(Rect Rect, int Index)> _rows = new();
    private readonly List<(Rect Rect, string Action)> _buttons = new();

    public PrismView(Func<List<PrismItem>> candidates, Action<PrismItem> activate, Func<string> user)
    { _candidates = candidates; _activate = activate; _user = user; }

    public bool IsOpen { get; private set; }
    public Layout Mode { get; private set; }
    public Rect Bounds { get; private set; }

    public void Open(Layout layout, Rect bar, int width, int height)
    {
        Mode = layout; IsOpen = true; _query = ""; _filter = 0; _selected = 0; _scroll = 0;
        _all = _candidates(); Refresh();
        Place(bar, width, height);
        Zenith.Core.Log.Write("prism", "opened " + (layout == Layout.Menu ? "menu" : "palette") + " candidates=" + _all.Count);
    }

    public void Close() { IsOpen = false; _rows.Clear(); _buttons.Clear(); }

    /// <summary>Keeps the menu above the bar and the palette in the upper third, wherever the
    /// display and the bar are now.</summary>
    public void Place(Rect bar, int width, int height)
    {
        if (Mode == Layout.Palette)
        {
            int w = Math.Min(680, width - 80);
            _maxHeight = Math.Min(560, height - height * 16 / 100 - 40);
            Bounds = new Rect((width - w) / 2, Math.Max(24, height * 16 / 100), w, _maxHeight);
        }
        else
        {
            int w = Math.Min(440, width - 2 * Theme.TaskbarMargin), h = Math.Min(560, bar.Y - 2 * Theme.TaskbarMargin);
            Bounds = new Rect(Math.Max(bar.X, Theme.TaskbarMargin), bar.Y - h - Theme.TaskbarMargin, w, h);
        }
    }

    private bool Browsing => Mode == Layout.Menu && _query.Length == 0 && _filter == 0;

    private void Refresh()
    {
        // The menu, before anything is typed, lists every app.
        _results = Browsing ? PrismSearch.Search("", _all, PrismKind.App, 64) : PrismSearch.Search(_query, _all, PrismSearch.Filters[_filter].Kind, 24);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _results.Count - 1));
        _scroll = Math.Min(_scroll, Math.Max(0, _selected));
    }

    /// <summary>Handles a key while open; true when it was used.</summary>
    public bool HandleKey(KeyEvent key)
    {
        switch (key.Key)
        {
            case ConsoleKeyEx.Escape: Close(); return true;
            case ConsoleKeyEx.Enter: Activate(_selected); return true;
            case ConsoleKeyEx.DownArrow: _selected = Math.Min(_selected + 1, _results.Count - 1); return true;
            case ConsoleKeyEx.UpArrow: _selected = Math.Max(_selected - 1, 0); return true;
            case ConsoleKeyEx.PageDown: _selected = Math.Min(_selected + 6, _results.Count - 1); return true;
            case ConsoleKeyEx.PageUp: _selected = Math.Max(_selected - 6, 0); return true;
            case ConsoleKeyEx.Tab:
                int n = PrismSearch.Filters.Length;
                _filter = (_filter + ((key.Modifiers & ConsoleModifiers.Shift) != 0 ? n - 1 : 1)) % n;
                _selected = 0; _scroll = 0; Refresh(); return true;
            case ConsoleKeyEx.Backspace:
                if (_query.Length > 0) { _query = (key.Modifiers & ConsoleModifiers.Control) != 0 ? "" : _query[..^1]; _selected = 0; _scroll = 0; Refresh(); }
                return true;
        }
        if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0) { return true; }
        char c = key.KeyChar;
        if (c >= ' ' && c <= '~' && _query.Length < PrismSearch.MaximumQuery)
        { _query += c; _selected = 0; _scroll = 0; Refresh(); }
        return true;
    }

    /// <summary>A click while open: a row or button acts; anywhere outside closes.</summary>
    public void HandleClick(int x, int y)
    {
        foreach (var (rect, index) in _rows) { if (rect.Contains(x, y)) { Activate(index); return; } }
        foreach (var (rect, action) in _buttons) { if (rect.Contains(x, y)) { Close(); _activate(new PrismItem(PrismKind.Action, action, "", "", action)); return; } }
        if (!Bounds.Contains(x, y)) { Close(); }
    }

    public int HoverKey(int x, int y)
    {
        for (int i = 0; i < _rows.Count; i++) { if (_rows[i].Rect.Contains(x, y)) { return i + 1; } }
        for (int i = 0; i < _buttons.Count; i++) { if (_buttons[i].Rect.Contains(x, y)) { return 100 + i; } }
        return 0;
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _results.Count) { return; }
        var item = _results[index];
        Close();
        Zenith.Core.Log.Write("prism", "open " + item.Kind + " " + item.Target);
        _activate(item);
    }

    // ---------- Drawing.

    /// <summary>Height of the result list: section headers and rows, plus a margin.</summary>
    private int ContentHeight()
    {
        if (_results.Count == 0) { return 52; }
        int h = 12; PrismKind? last = null;
        foreach (var item in _results) { if (item.Kind != last) { h += Header; last = item.Kind; } h += Row; }
        return h;
    }

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        if (!IsOpen) { return; }
        _rows.Clear(); _buttons.Clear();
        // The palette grows downward with its results; the search field never moves.
        if (Mode == Layout.Palette) { Bounds = new Rect(Bounds.X, Bounds.Y, Bounds.W, Math.Min(_maxHeight, 56 + ContentHeight() + 36)); }
        Rect b = Bounds;
        surface.DrawShadow(b, 12, 22, 110, 8);
        surface.FillRoundRect(b, 12, Theme.Menu);
        surface.StrokeRoundRect(b, 12, Theme.PanelBorder);
        int top = Mode == Layout.Palette ? DrawSearch(surface, new Rect(b.X, b.Y, b.W, 56), false)
            : DrawSearch(surface, new Rect(b.X + Pad, b.Y + Pad, b.W - 2 * Pad, 42), true) + Pad;
        int footer = Mode == Layout.Palette ? 36 : 60;
        DrawResults(surface, new Rect(b.X, top, b.W, b.Bottom - footer - top), mouseX, mouseY);
        if (Mode == Layout.Palette) { DrawHints(surface, new Rect(b.X, b.Bottom - footer, b.W, footer)); }
        else { DrawPower(surface, new Rect(b.X, b.Bottom - footer, b.W, footer), mouseX, mouseY); }
    }

    /// <summary>The search field; returns the y below it.</summary>
    private int DrawSearch(Surface s, Rect r, bool field)
    {
        if (field) { s.FillRoundRect(r, 10, Theme.Raised); s.StrokeRoundRect(r, 10, Theme.PanelBorder); }
        int size = field ? Theme.TextBody + 1 : 18, cy = r.Y + (r.H - size) / 2 - 2;
        int glyph = field ? 16 : 24;
        if (!Icons.Draw(s, "search", r.X + 16, r.Y + (r.H - glyph) / 2, glyph, Theme.TextSecondary))
        { s.DrawText("?", Fonts.SemiBold, size, Theme.TextSecondary, r.X + 18, cy); }
        int tx = r.X + 48;
        string filter = PrismSearch.Filters[_filter].Label;
        int pillW = Fonts.Regular.MeasureString(filter, Theme.TextSmall) + 20;
        Rect pill = new(r.Right - pillW - 14, r.Y + (r.H - 22) / 2, pillW, 22);
        if (_query.Length == 0)
        {
            string hint = Mode == Layout.Menu ? "Search apps, settings and more" : "Search apps, windows, settings and actions";
            s.DrawText(hint, Fonts.Regular, size, Theme.TextSecondary, tx, cy);
            s.FillRect(new Rect(tx - 2, r.Y + (r.H - size - 6) / 2, 2, size + 6), Theme.Accent);
        }
        else
        {
            string shown = _query;
            while (shown.Length > 0 && Fonts.Regular.MeasureString(shown, size) > pill.X - tx - 12) { shown = shown[1..]; }
            s.DrawText(shown, Fonts.Regular, size, Theme.TextPrimary, tx, cy);
            int caret = tx + Fonts.Regular.MeasureString(shown, size) + 1;
            s.FillRect(new Rect(caret, r.Y + (r.H - size - 6) / 2, 2, size + 6), Theme.Accent);
        }
        if (!Browsing || Mode == Layout.Palette)
        {
            s.FillRoundRect(pill, 11, Theme.Raised);
            s.DrawTextCentered(filter, Fonts.SemiBold, Theme.TextSmall, Theme.TextSecondary, pill, true);
        }
        if (!field) { s.FillRect(new Rect(r.X, r.Bottom - 1, r.W, 1), Theme.TitleDivider); }
        return r.Bottom;
    }

    private void DrawResults(Surface s, Rect area, int mouseX, int mouseY)
    {
        if (_results.Count == 0)
        {
            s.DrawText(_query.Length == 0 ? "Nothing here yet." : "No matches. Tab changes the filter.", Fonts.Regular, Theme.TextBody, Theme.TextSecondary, area.X + 20, area.Y + 16);
            return;
        }
        // Lay out sections and rows, then scroll so the selection stays visible.
        var layout = new List<(int Y, int Index, PrismKind? Section)>(); int y = 6; PrismKind? last = null;
        for (int i = 0; i < _results.Count; i++)
        {
            var kind = _results[i].Kind;
            if (kind != last) { layout.Add((y, -1, kind)); y += Header; last = kind; }
            layout.Add((y, i, null)); y += Row;
        }
        int selectedY = 0; foreach (var entry in layout) { if (entry.Index == _selected) { selectedY = entry.Y; } }
        if (selectedY - _scroll > area.H - Row - 4) { _scroll = selectedY - (area.H - Row - 4); }
        if (selectedY - Header - _scroll < 0) { _scroll = Math.Max(0, selectedY - Header - 6); }
        foreach (var (rowY, index, section) in layout)
        {
            int screenY = area.Y + rowY - _scroll;
            if (screenY < area.Y || screenY + (section is null ? Row : Header) > area.Bottom) { continue; }
            if (section is PrismKind k)
            {
                string title = Browsing ? "Applications" : PrismSearch.SectionTitles[(int)k];
                s.DrawText(title, Fonts.SemiBold, Theme.TextSmall, Theme.TextSecondary, area.X + 20, screenY + 8);
                continue;
            }
            Rect row = new(area.X + 8, screenY, area.W - 16, Row - 2);
            DrawRow(s, row, _results[index], index == _selected, row.Contains(mouseX, mouseY));
            _rows.Add((row, index));
        }
        if (y > area.H)
        {
            int track = area.H - 12, thumb = Math.Max(24, track * area.H / y), at = area.Y + 6 + (track - thumb) * _scroll / Math.Max(1, y - area.H);
            s.FillRoundRect(new Rect(area.Right - 6, at, 3, thumb), 2, Theme.TextMuted);
        }
    }

    private static void DrawRow(Surface s, Rect row, PrismItem item, bool selected, bool hover)
    {
        if (selected)
        {
            s.FillRoundRect(row, 8, Theme.AccentSoft);
            s.FillRoundRect(new Rect(row.X, row.Y + 9, 3, row.H - 18), 1, Theme.Accent);
        }
        else if (hover) { s.FillRoundRect(row, 8, Theme.Hover); }
        Rect tile = new(row.X + 12, row.Y + (row.H - 32) / 2, 32, 32);
        s.FillRoundRect(tile, 8, Theme.Raised);
        if (!Icons.Draw(s, item.Icon, tile.X + 8, tile.Y + 8, 16, selected ? Theme.Accent : Theme.TextPrimary))
        { s.DrawTextCentered(item.Title.Length > 0 ? item.Title[..1] : "?", Fonts.SemiBold, Theme.TextBody, Theme.Accent, tile, true); }
        int tx = tile.Right + 12, right = row.Right - (selected ? 70 : 12);
        bool mono = item.Kind is PrismKind.Calculator;
        FontFace titleFont = mono ? Fonts.Mono : Fonts.SemiBold;
        s.DrawText(Fit(item.Title, titleFont, Theme.TextBody, right - tx), titleFont, Theme.TextBody, Theme.TextPrimary, tx, row.Y + 6);
        s.DrawText(Fit(item.Subtitle, Fonts.Regular, Theme.TextSmall, right - tx), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, tx, row.Y + 25);
        if (selected)
        {
            Rect key = new(row.Right - 60, row.Y + (row.H - 22) / 2, 48, 22);
            s.FillRoundRect(key, 6, Theme.Raised); s.StrokeRoundRect(key, 6, Theme.PanelBorder);
            s.DrawTextCentered("Enter", Fonts.SemiBold, Theme.TextSmall, Theme.TextPrimary, key, true);
        }
    }

    private static string Fit(string text, FontFace font, int size, int width)
    {
        if (width <= 0) { return ""; }
        if (font.MeasureString(text, size) <= width) { return text; }
        while (text.Length > 0 && font.MeasureString(text + "...", size) > width) { text = text[..^1]; }
        return text + "...";
    }

    private void DrawHints(Surface s, Rect r)
    {
        s.FillRect(new Rect(r.X, r.Y, r.W, 1), Theme.TitleDivider);
        int x = r.X + 18, y = r.Y + 11;
        foreach (string hint in new[] { "Enter open", "Tab filter", "> command", "Esc close" })
        {
            s.DrawText(hint, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, y);
            x += Fonts.Regular.MeasureString(hint, Theme.TextSmall) + 22;
        }
        s.DrawText("Prism", Fonts.SemiBold, Theme.TextSmall, Theme.TextSecondary, r.Right - 18 - Fonts.SemiBold.MeasureString("Prism", Theme.TextSmall), y);
    }

    /// <summary>The menu's bottom bar: who is signed in, then log out, restart and shut down.</summary>
    private void DrawPower(Surface s, Rect r, int mouseX, int mouseY)
    {
        s.FillRect(new Rect(r.X, r.Y, r.W, 1), Theme.TitleDivider);
        string user = _user();
        Rect avatar = new(r.X + 16, r.Y + (r.H - 30) / 2, 30, 30);
        s.FillRoundRect(avatar, 15, Theme.AccentSoft);
        s.DrawTextCentered(user.Length > 0 ? char.ToUpperInvariant(user[0]).ToString() : "?", Fonts.SemiBold, Theme.TextBody, Theme.Accent, avatar, true);
        s.DrawText(user, Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, avatar.Right + 10, r.Y + (r.H - Theme.TextBody) / 2 - 2);
        string? blocked = !Zenith.Core.Security.CredentialContext.Current.IsRoot ? "root" : Zenith.Core.PowerControl.BlockedReason;
        int x = r.Right - 12;
        foreach (var (label, action, power) in new[] { ("Shut down", "shutdown", true), ("Restart", "restart", true), ("Log out", "logout", false) })
        {
            int w = Fonts.Regular.MeasureString(label, Theme.TextSmall) + 22; x -= w;
            Rect button = new(x, r.Y + (r.H - 30) / 2, w, 30); x -= 6;
            bool enabled = !power || blocked is null;
            if (enabled && button.Contains(mouseX, mouseY)) { s.FillRoundRect(button, 8, Theme.Hover); }
            s.StrokeRoundRect(button, 8, Theme.PanelBorder);
            s.DrawTextCentered(label, Fonts.Regular, Theme.TextSmall, enabled ? Theme.TextPrimary : Theme.TextMuted, button, true);
            if (enabled) { _buttons.Add((button, action)); }
        }
        if (Zenith.Core.Sessions.SessionRuntime.Message is string message)
        { s.DrawText(message, Fonts.Regular, 12, Theme.Danger, Bounds.X + 12, Math.Max(0, Bounds.Y - 20)); }
    }
}
