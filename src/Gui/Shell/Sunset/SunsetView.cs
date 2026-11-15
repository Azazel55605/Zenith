using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Sessions;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell.Sunset;

/// <summary>What Sunset needs from the desktop: who and where, what is in the way, and how to act.</summary>
internal interface ISunsetHost
{
    string Who { get; }
    List<SessionBlocker> Blockers();
    string? LockUnavailable { get; }
    string? LogoutUnavailable { get; }
    string? PowerUnavailable { get; }
    /// <summary>Carries out a choice; returns why it could not, or null when it is under way.</summary>
    string? Act(SunsetAction action);
    void Show(object target);
}

/// <summary>Sunset (docs/design/sunset-concept.svg): the small popup for lock, log out, restart and shut
/// down, drawn by the desktop over a dimmed screen. One key per action (L, O, R, S), arrows move, Enter or a
/// click acts at once, Esc cancels. It names what would be lost before anything is pressed, with a button to
/// go to each window, and keeps Lock available because it interrupts nothing.</summary>
internal sealed class SunsetView
{
    private const int Pad = 20, Tile = 110, Gap = 9, TileHeight = 108, Head = 64, Footer = 34, Item = 23;
    public const int CardWidth = 2 * Pad + 4 * Tile + 3 * Gap;
    private readonly ISunsetHost _host;
    private List<SessionBlocker> _blockers = new();
    private SunsetChoice[] _choices = Array.Empty<SunsetChoice>();
    private int _focus;
    private string? _message;
    private long _refreshedAt;
    private readonly List<(Rect Rect, int Id)> _hits = new();

    public SunsetView(ISunsetHost host) => _host = host;

    // The card uses the desktop's own material, like the taskbar and Prism (solid when materials are off).
    private readonly HostMaterial _material = new(560 * 520);
    public void Dispose() => _material.Dispose();

    public bool IsOpen { get; private set; }
    public Rect Card { get; private set; }

    public void Open(SunsetAction preferred, int width, int height)
    {
        Refresh();
        _focus = SunsetPlan.Initial(_choices, preferred);
        _message = null; IsOpen = true;
        Place(width, height);
        Zenith.Core.Log.Write("sunset", "opened blockers=" + _blockers.Count + " focus=" + _choices[_focus].Action);
    }

    public void Close() { if (IsOpen) { IsOpen = false; _hits.Clear(); Zenith.Core.Log.Write("sunset", "closed"); } }

    public void Place(int width, int height)
    {
        int h = Height;
        Card = new Rect((width - CardWidth) / 2, Math.Max(16, (height - h) / 2 - 24), CardWidth, h);
    }

    private int Height
    {
        get
        {
            int h = Pad + Head + TileHeight + 16;
            if (_blockers.Count > 0) { h += 16 + 22 + Math.Min(_blockers.Count, SunsetPlan.MaximumBlockers) * Item + 12 + 12; }
            else { h += 22 + 10; }
            if (_message is not null) { h += 22; }
            return h + Footer + Pad;
        }
    }

    private void Refresh()
    {
        _blockers = _host.Blockers();
        _choices = SunsetPlan.Choices(_blockers, _host.LockUnavailable, _host.LogoutUnavailable, _host.PowerUnavailable);
        _refreshedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Twice a second, looks again at what is in the way (a file saved, a command finished); true when
    /// the popup needs drawing again.</summary>
    public bool Tick(int width, int height)
    {
        if (!IsOpen || System.Diagnostics.Stopwatch.GetTimestamp() - _refreshedAt < System.Diagnostics.Stopwatch.Frequency / 2) { return false; }
        int before = _blockers.Count; string?[] was = Array.ConvertAll(_choices, c => c.Disabled);
        string key = Key(_blockers);
        Refresh();
        bool changed = before != _blockers.Count || key != Key(_blockers);
        for (int i = 0; i < _choices.Length; i++) { changed |= was[i] != _choices[i].Disabled; }
        if (!changed) { return false; }
        if (_choices[_focus].Disabled is not null) { _focus = SunsetPlan.Initial(_choices, SunsetAction.Lock); }
        Place(width, height);
        Zenith.Core.Log.Write("sunset", "blockers=" + _blockers.Count);
        return true;
    }

    private static string Key(List<SessionBlocker> blockers)
    {
        var b = new System.Text.StringBuilder();
        foreach (var x in blockers) { b.Append(x.Owner).Append('\u0001').Append(x.Reason).Append('\u0002'); }
        return b.ToString();
    }

    public void Key(KeyEvent key, int width, int height)
    {
        switch (key.Key)
        {
            case ConsoleKeyEx.Escape: Close(); return;
            case ConsoleKeyEx.LeftArrow: case ConsoleKeyEx.UpArrow: _focus = SunsetPlan.Move(_choices, _focus, -1); return;
            case ConsoleKeyEx.RightArrow: case ConsoleKeyEx.DownArrow: _focus = SunsetPlan.Move(_choices, _focus, 1); return;
            case ConsoleKeyEx.Tab: _focus = SunsetPlan.Move(_choices, _focus, (key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1); return;
            case ConsoleKeyEx.Enter: case ConsoleKeyEx.Spacebar: Choose(_focus, width, height); return;
        }
        if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0) { return; }
        int index = SunsetPlan.ForKey(_choices, key.KeyChar);
        if (index >= 0) { Choose(index, width, height); }
    }

    public void Click(int x, int y, int width, int height)
    {
        foreach (var (rect, id) in _hits)
        {
            if (!rect.Contains(x, y)) { continue; }
            if (id == 99) { Close(); return; }
            if (id < 4) { Choose(id, width, height); return; }
            int blocker = id - 100;
            if (blocker >= 0 && blocker < _blockers.Count && _blockers[blocker].Target is object target)
            { Zenith.Core.Log.Write("sunset", "show " + _blockers[blocker].Owner); Close(); _host.Show(target); }
            return;
        }
        if (!Card.Contains(x, y)) { Close(); }
    }

    public long HoverKey(int x, int y)
    {
        foreach (var (rect, id) in _hits) { if (rect.Contains(x, y)) { return id + 1; } }
        return 0;
    }

    private void Choose(int index, int width, int height)
    {
        Refresh();
        var choice = _choices[index];
        if (choice.Disabled is not null)
        {
            _focus = index; _message = choice.Label + ": " + choice.Disabled; Place(width, height);
            Zenith.Core.Log.Write("sunset", "refused " + choice.Action + ": " + choice.Disabled);
            return;
        }
        Zenith.Core.Log.Write("sunset", "chose " + choice.Action);
        IsOpen = false; _hits.Clear();
        string? failed = _host.Act(choice.Action);
        if (failed is null) { return; }
        // The session refused at the last moment (something started meanwhile): stay, and say why.
        IsOpen = true; _focus = index; _message = failed; Refresh(); Place(width, height);
        Zenith.Core.Log.Write("sunset", choice.Action + " refused: " + failed);
    }

    public void Draw(Surface s, int mouseX, int mouseY)
    {
        if (!IsOpen) { return; }
        _hits.Clear();
        s.FillRect(s.Bounds, 0x99000000U);
        Rect card = Card;
        _material.Capture(s, card.Intersect(s.Bounds), Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
        s.DrawShadow(card, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
        _material.Paint(s, card.Intersect(s.Bounds), Theme.WindowBackground | 0xff000000U);
        s.StrokeRoundRect(card, Theme.WindowRadius, Theme.WindowBorder);
        int x = card.X + Pad, y = card.Y + Pad, inner = card.W - 2 * Pad;

        // Who: an avatar, the question, then "user on host · up 3 h 12 min".
        Rect avatar = new(x, y, 44, 44);
        s.FillRoundRect(avatar, 22, Theme.AccentSoft);
        if (!Icons.Draw(s, "user", avatar.X + 12, avatar.Y + 12, 20, Theme.Accent))
        { s.DrawTextCentered(_host.Who.Length > 0 ? char.ToUpperInvariant(_host.Who[0]).ToString() : "?", Fonts.SemiBold, Theme.TextBody, Theme.Accent, avatar, true); }
        s.DrawText("End this session?", Fonts.SemiBold, 16, Theme.TextPrimary, avatar.Right + 14, y + 2);
        long up = System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        s.DrawText(Zenith.Gui.Shell.Portal.PortalHost.Fit(_host.Who + " on " + Zenith.Core.Network.NetworkService.Hostname + " · " + SunsetPlan.Uptime(up), inner - 90, 13),
            Fonts.Regular, 13, Theme.TextSecondary, avatar.Right + 14, y + 25);
        Rect close = new(card.Right - Pad - 24, y, 24, 24);
        if (close.Contains(mouseX, mouseY)) { s.FillRoundRect(close, 6, Theme.Hover); }
        if (!Icons.Draw(s, "x", close.X + 4, close.Y + 4, 16, Theme.TextSecondary))
        { s.DrawTextCentered("x", Fonts.Regular, Theme.TextBody, Theme.TextSecondary, close, true); }
        _hits.Add((close, 99));
        y += Head;

        // The four tiles.
        for (int i = 0; i < _choices.Length; i++)
        {
            var c = _choices[i];
            Rect tile = new(x + i * (Tile + Gap), y, Tile, TileHeight);
            bool enabled = c.Disabled is null, focused = i == _focus;
            s.FillRoundRect(tile, Theme.ControlRadius, focused && enabled ? Theme.AccentSoft : Theme.Raised | 0xff000000U);
            if (enabled && !focused && tile.Contains(mouseX, mouseY)) { s.FillRoundRect(tile, Theme.ControlRadius, Theme.Hover); }
            s.StrokeRoundRect(tile, Theme.ControlRadius, focused && enabled ? Theme.Accent : Theme.WindowBorder);
            uint ink = !enabled ? Theme.TextMuted : focused ? Theme.Accent : Theme.TextPrimary;
            if (!Icons.Draw(s, c.Icon, tile.X + (Tile - 26) / 2, tile.Y + 18, 26, ink)) { DrawFallback(s, c.Action, tile.X + (Tile - 26) / 2, tile.Y + 18, ink); }
            s.DrawTextCentered(c.Label, Fonts.SemiBold, 13, enabled ? Theme.TextPrimary : Theme.TextMuted, new Rect(tile.X, tile.Y + 52, tile.W, 20), true);
            Rect cap = new(tile.X + (Tile - 18) / 2, tile.Y + 78, 18, 18);
            s.FillRoundRect(cap, 4, Theme.Panel | 0xff000000U);
            s.StrokeRoundRect(cap, 4, Theme.WindowBorder);
            s.DrawTextCentered(c.Key.ToString(), Fonts.Mono, 11, enabled ? Theme.TextSecondary : Theme.TextMuted, cap, true);
            _hits.Add((tile, i));
        }
        y += TileHeight + 16;

        if (_blockers.Count == 0)
        {
            s.DrawText("Everything is saved. Nothing is running.", Fonts.Regular, 13, Theme.TextSecondary, x, y);
            y += 32;
            if (_message is not null) { s.DrawText(Zenith.Gui.Shell.Portal.PortalHost.Fit(_message, inner, 12), Fonts.Regular, 12, Theme.Danger, x, y - 8); y += 22; }
            if (!Icons.Draw(s, "check", x, y + 1, 16, Success)) { s.DrawText("+", Fonts.SemiBold, 13, Success, x + 3, y); }
            s.DrawText("Safe to leave", Fonts.SemiBold, 13, Success, x + 22, y);
            string hint = "Esc cancels · arrows move · Enter confirms";
            s.DrawText(hint, Fonts.Regular, 12, Theme.TextMuted, card.Right - Pad - Fonts.Regular.MeasureString(hint, 12), y + 1);
            return;
        }

        // What is in the way, then a button for each window that can deal with it.
        int shown = Math.Min(_blockers.Count, SunsetPlan.MaximumBlockers);
        Rect panel = new(x, y, inner, 16 + 22 + shown * Item + 8);
        s.FillRoundRect(panel, Theme.ControlRadius, Theme.IsLight ? 0xFFF3E6CCU : 0xFF3A3324U);
        uint warn = Theme.IsLight ? 0xFFB7791FU : 0xFFE5B547U;
        if (!Icons.Draw(s, "triangle-alert", panel.X + 14, panel.Y + 14, 16, warn)) { s.DrawText("!", Fonts.SemiBold, 14, warn, panel.X + 18, panel.Y + 13); }
        s.DrawText(SunsetPlan.Summary(_blockers.Count), Fonts.SemiBold, 13, Theme.TextPrimary, panel.X + 36, panel.Y + 14);
        int ly = panel.Y + 16 + 24;
        for (int i = 0; i < shown; i++)
        {
            var b = _blockers[i];
            if (!Icons.Draw(s, b.Unsaved ? "file" : "terminal", panel.X + 36, ly + 1, 14, Theme.TextSecondary)) { }
            s.DrawText(Zenith.Gui.Shell.Portal.PortalHost.Fit(b.Owner + ": " + b.Reason, panel.W - 80, 13), Fonts.Regular, 13, Theme.TextSecondary, panel.X + 58, ly);
            ly += Item;
        }
        y = panel.Bottom + 12;
        if (_message is not null) { s.DrawText(Zenith.Gui.Shell.Portal.PortalHost.Fit(_message, inner, 12), Fonts.Regular, 12, Theme.Danger, x, y); y += 22; }
        int bx = x, shownButtons = 0;
        for (int i = 0; i < _blockers.Count && shownButtons < 2; i++)
        {
            if (_blockers[i].Target is null) { continue; }
            string label = "Show " + ShortName(_blockers[i].Owner);
            int w = Math.Min(Math.Max(Fonts.SemiBold.MeasureString(label, 13) + 28, 120), 180);
            Rect button = new(bx, y, w, 32);
            AppStyle.Button(s, button, Zenith.Gui.Shell.Portal.PortalHost.Fit(label, w - 16, Theme.TextBody));
            if (button.Contains(mouseX, mouseY)) { s.FillRoundRect(button, Theme.ControlRadius, Theme.Hover); }
            _hits.Add((button, 100 + i)); bx += w + 8; shownButtons++;
        }
        string note = _choices[0].Disabled is null ? "Lock stays available" : "Esc cancels";
        s.DrawText(note, Fonts.Regular, 12, Theme.TextMuted, card.Right - Pad - Fonts.Regular.MeasureString(note, 12), y + 9);
    }

    private static uint Success => Theme.IsLight ? 0xFF1F7A45U : 0xFF6FCF97U;

    /// <summary>"Editor — notes.txt •" → "Editor"; "Documents — Horizon" → "Horizon" is not guessable, so the
    /// first part is used unless it looks like a file or folder name.</summary>
    internal static string ShortName(string owner)
    {
        int dash = owner.IndexOf(" — ", StringComparison.Ordinal);
        if (dash < 0) { return owner; }
        string first = owner[..dash], last = owner[(dash + 3)..].TrimEnd(' ', '•');
        return first.Contains('.') || first.Contains('/') ? last : first;
    }

    /// <summary>Simple shapes when zenith-icons is not installed: the label and key still say what each does.</summary>
    private static void DrawFallback(Surface s, SunsetAction action, int x, int y, uint ink)
    {
        switch (action)
        {
            case SunsetAction.Lock:
                s.StrokeRoundRect(new Rect(x + 7, y + 2, 12, 14), 6, ink);
                s.FillRoundRect(new Rect(x + 3, y + 11, 20, 14), 3, ink);
                break;
            case SunsetAction.LogOut:
                s.StrokeRoundRect(new Rect(x + 2, y + 3, 12, 20), 2, ink);
                s.FillRect(new Rect(x + 9, y + 12, 15, 2), ink);
                s.FillRect(new Rect(x + 20, y + 9, 2, 8), ink);
                break;
            case SunsetAction.Restart:
                s.StrokeRoundRect(new Rect(x + 3, y + 3, 20, 20), 10, ink);
                s.FillRect(new Rect(x + 2, y + 2, 7, 2), ink);
                break;
            default:
                s.StrokeRoundRect(new Rect(x + 3, y + 5, 20, 20), 10, ink);
                s.FillRect(new Rect(x + 12, y, 2, 13), ink);
                break;
        }
    }
}
