using System;
using System.Collections.Generic;
using System.Diagnostics;
using Zenith.Core.Configuration;
using Zenith.Core.Notifications;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>Up to three notification cards stacked in the chosen work-area corner.
/// Each slides in from its edge and times out unless the pointer is holding it.
/// Suppressed popups stay in the notification history.</summary>
internal sealed class Toasts
{
    public const int Width = 360, Gap = 8, Margin = 12, Pad = 14, MaxShown = 3;
    private const long SlideMicros = 180_000, HoldAfterHoverMicros = 1_500_000;
    private sealed class Card
    {
        public required NotificationCenter.Note Note;
        public long Shown, Leaving, Deadline;
        public string[] Lines = Array.Empty<string>();
        public Rect Bounds;
        public bool Logged;
    }
    private readonly List<Card> _cards = new();
    private readonly Queue<NotificationCenter.Note> _waiting = new();
    private static readonly SettingsService.NotificationPreferences DefaultPreferences = new();
    private bool _fromLeft;
    private static long Now => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;
    public static Func<int> AnimationPercent = () => 100;

    public bool IsShown => _cards.Count > 0;
    /// <summary>A card is still sliding: the desktop keeps drawing.</summary>
    public bool Moving
    {
        get
        {
            long now = Now, slide = SlideMicros * AnimationPercent() / 100;
            foreach (var c in _cards) { if (now - c.Shown < slide || (c.Leaving != 0 && now - c.Leaving < slide)) { return true; } }
            return false;
        }
    }

    /// <summary>Takes new notifications, ends cards whose time ran out and lays the stack out in the work area.
    /// True when something on screen changed.</summary>
    /// <summary>Carries out a button (opens an app, restarts through Sunset, tells the sending program); set by the desktop.</summary>
    public Action<NotificationCenter.NoteAction, NotificationCenter.Note, int>? RunAction;
    public const int ButtonHeight = 28;

    public bool Tick(Rect work, int mouseX, int mouseY, bool historyOpen, int frontPid, bool fullscreen)
    {
        var settings = SystemConfiguration.Settings?.Notifications ?? DefaultPreferences;
        if (historyOpen || !settings.AllowsPopups(fullscreen))
        {
            NotificationCenter.TakeNew();
            bool hadCards = _cards.Count > 0;
            _cards.Clear(); _waiting.Clear();
            return hadCards;
        }
        bool changed = false;
        _fromLeft = settings.FromLeft;
        foreach (var note in NotificationCenter.TakeNew())
        {
            if (!settings.AllowsSender(note.SenderPid, frontPid)) { Zenith.Core.Log.Write("notify", "toast " + note.Id + " skipped: its app is in front"); continue; }
            _waiting.Enqueue(note); while (_waiting.Count > 8) { _waiting.Dequeue(); }
        }
        long now = Now, slide = SlideMicros * AnimationPercent() / 100;
        long hold = (long)SettingsService.PopupSeconds[settings.Time] * 1_000_000;
        bool sounded = false;
        while (_cards.Count < MaxShown && _waiting.Count > 0)
        {
            var note = _waiting.Dequeue();
            if (!settings.AllowsSender(note.SenderPid, frontPid)) { continue; }
            _cards.Insert(0, new Card { Note = note, Shown = now, Deadline = now + hold });
            if (!sounded && (settings.Flags & SettingsService.PopupSound) != 0) { sounded = true; Zenith.Core.Audio.SystemSounds.Play("notification"); }
            changed = true;
        }
        changed |= Layout(work, settings.FromTop);
        bool removed = false;
        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            var c = _cards[i];
            if (c.Leaving == 0)
            {
                if ((settings.Flags & SettingsService.PopupHoldOnHover) != 0 && DrawnBounds(c, now).Contains(mouseX, mouseY)) { c.Deadline = Math.Max(c.Deadline, now + HoldAfterHoverMicros); }
                else if (now >= c.Deadline) { c.Leaving = now; changed = true; }
            }
            else if (now - c.Leaving >= slide) { _cards.RemoveAt(i); removed = true; }
        }
        if (removed) { Layout(work, settings.FromTop); changed = true; }
        // Where each new card landed, once: gates turn this into clicks on it and its buttons.
        foreach (var c in _cards)
        {
            if (c.Logged) { continue; }
            c.Logged = true; Rect b = c.Bounds;
            Zenith.Core.Log.Write("notify", "toast " + c.Note.Id + " shown at " + b.X + "," + b.Y + " " + b.W + "x" + b.H + " buttons=" + (c.Note.Actions?.Length ?? 0));
        }
        return changed || Moving;
    }

    private bool Layout(Rect work, bool fromTop)
    {
        int width = Math.Min(Width, work.W - 2 * Margin), inner = width - 2 * Pad - 44;
        int x = _fromLeft ? work.X + Margin : work.Right - Margin - width;
        int edge = fromTop ? work.Y + Margin : work.Bottom - Margin;
        bool changed = false;
        foreach (var c in _cards)
        {
            if (c.Bounds.W != width) { c.Lines = Wrap(c.Note.Body, inner, 2); }
            int height = Pad + 16 + 20 + c.Lines.Length * 18 + Pad + (c.Note.Actions is { Length: > 0 } ? ButtonHeight + 8 : 0);
            int y = fromTop ? edge : edge - height;
            changed |= c.Bounds.X != x || c.Bounds.Y != y || c.Bounds.W != width || c.Bounds.H != height;
            c.Bounds = new Rect(x, y, width, height);
            edge += (fromTop ? 1 : -1) * (height + Gap);
        }
        return changed;
    }

    /// <summary>How far from its place a card is drawn while entering or leaving.</summary>
    private int Offset(Card c, long now)
    {
        long slide = SlideMicros * AnimationPercent() / 100;
        int distance = c.Bounds.W + Margin + Theme.ShadowSize;
        if (slide <= 0) { return c.Leaving != 0 ? distance : 0; }
        if (c.Leaving != 0)
        {
            long t = Math.Min(slide, now - c.Leaving);
            return distance * Zenith.Gui.Motion.MotionCurve.EaseOut(Zenith.Gui.Motion.MotionCurve.Linear(t, slide)) / Zenith.Gui.Motion.MotionCurve.One;
        }
        long e = now - c.Shown;
        if (e >= slide) { return 0; }
        return distance - distance * Zenith.Gui.Motion.MotionCurve.EaseOut(Zenith.Gui.Motion.MotionCurve.Linear(e, slide)) / Zenith.Gui.Motion.MotionCurve.One;
    }

    private Rect DrawnBounds(Card card, long now) => card.Bounds.Offset((_fromLeft ? -1 : 1) * Offset(card, now), 0);

    public bool Contains(int x, int y)
    {
        long now = Now;
        foreach (var c in _cards) { if (c.Leaving == 0 && DrawnBounds(c, now).Contains(x, y)) { return true; } }
        return false;
    }

    /// <summary>The buttons of a card in place: right-aligned at its bottom.</summary>
    internal static Rect[] Buttons(Rect card, int count, int pad = Pad)
    {
        var result = new Rect[count];
        int w = Math.Min(140, (card.W - 2 * pad - 44 - 8 * (count - 1)) / Math.Max(1, count)), x = card.Right - pad;
        for (int i = count - 1; i >= 0; i--) { x -= w; result[i] = new Rect(x, card.Bottom - pad - ButtonHeight, w, ButtonHeight); x -= 8; }
        return result;
    }
    /// <summary>A click on a card: on a button runs it, anywhere else dismisses the card (sliding out). True when the
    /// click landed on one.</summary>
    public bool HandleClick(int x, int y)
    {
        long now = Now;
        foreach (var c in _cards)
        {
            Rect drawn = DrawnBounds(c, now);
            if (c.Leaving == 0 && drawn.Contains(x, y))
            {
                c.Leaving = now;
                if (c.Note.Actions is { Length: > 0 } actions)
                {
                    var rects = Buttons(drawn, actions.Length);
                    for (int i = 0; i < rects.Length; i++)
                    {
                        if (!rects[i].Contains(x, y)) { continue; }
                        var action = NotificationCenter.Activate(c.Note.Id, i);
                        if (action is not null) { RunAction?.Invoke(action, c.Note, i); }
                        return true;
                    }
                }
                Zenith.Core.Log.Write("notify", "toast " + c.Note.Id + " dismissed");
                return true;
            }
        }
        return false;
    }

    public void Draw(Surface s, int mouseX, int mouseY)
    {
        long now = Now;
        foreach (var c in _cards)
        {
            Rect r = DrawnBounds(c, now);
            if (r.Right <= 0 || r.X >= s.Width) { continue; }
            s.DrawShadow(r, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
            s.FillRoundRect(r, Theme.WindowRadius, Theme.WindowBackground | 0xFF000000u);
            s.StrokeRoundRect(r, Theme.WindowRadius, Theme.WindowBorder);
            Rect badge = new(r.X + Pad, r.Y + Pad, 32, 32);
            s.FillRoundRect(badge, 9, Theme.AccentSoft);
            string icon = c.Note.Icon.Length > 0 ? c.Note.Icon : "info";
            // Without the icon package: the Zenith mark, as the permission dialog does.
            if (!Icons.Draw(s, icon, badge.X + 6, badge.Y + 6, 20, Theme.Accent) && !Icons.Draw(s, "info", badge.X + 6, badge.Y + 6, 20, Theme.Accent))
            { Logo.Draw(s, new Rect(badge.X + 6, badge.Y + 6, 20, 20)); }
            bool hovered = r.Contains(mouseX, mouseY);
            int x = badge.Right + 12, right = r.Right - Pad - (hovered ? 24 : 0);
            s.DrawText(Fit(c.Note.Source, right - x, Theme.TextSmall, Fonts.Regular), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, r.Y + Pad - 2);
            s.DrawText(Fit(c.Note.Title, right - x, Theme.TextBody, Fonts.SemiBold), Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, x, r.Y + Pad + 14);
            int y = r.Y + Pad + 36;
            foreach (string line in c.Lines) { s.DrawText(line, Fonts.Regular, Theme.TextSmall + 1, Theme.TextSecondary, x, y); y += 18; }
            if (c.Note.Actions is { Length: > 0 } actions)
            {
                var rects = Buttons(r, actions.Length);
                for (int i = 0; i < rects.Length; i++)
                {
                    bool over = rects[i].Contains(mouseX, mouseY);
                    // The first button is the main one: filled with the accent, the second plain.
                    s.FillRoundRect(rects[i], Theme.ControlRadius, i == 0 ? (over ? Theme.Accent & 0xE0FFFFFFu : Theme.Accent) : over ? Theme.Pressed : Theme.Hover);
                    s.DrawTextCentered(Fit(actions[i].Label, rects[i].W - 12, Theme.TextSmall + 1, Fonts.Regular), Fonts.Regular, Theme.TextSmall + 1, i == 0 ? Theme.OnAccent : Theme.TextPrimary, rects[i], true);
                }
            }
            // The close mark shows under the pointer; a click anywhere on the card dismisses it.
            if (c.Leaving == 0 && hovered && !Icons.Draw(s, "x", r.Right - Pad - 16, r.Y + Pad, 16, Theme.TextSecondary))
            {
                // Drawn by hand when the icon package is missing: two strokes, 2 px wide.
                int cx = r.Right - Pad - 16, cy = r.Y + Pad;
                for (int k = 3; k < 13; k++)
                {
                    s.FillRect(new Rect(cx + k, cy + k, 2, 2), Theme.TextSecondary);
                    s.FillRect(new Rect(cx + 14 - k, cy + k, 2, 2), Theme.TextSecondary);
                }
            }
        }
    }

    // ---------- Text: a line fitted with an ellipsis, a body wrapped into at most a few lines.
    private static string Fit(string text, int width, int size, FontFace font)
    {
        if (font.MeasureString(text, size) <= width) { return text; }
        int n = text.Length;
        while (n > 0 && font.MeasureString(text[..n] + "...", size) > width) { n--; }
        return text[..n].TrimEnd() + "...";
    }
    internal static string[] Wrap(string text, int width, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text)) { return Array.Empty<string>(); }
        var lines = new List<string>(); string line = "";
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            string next = line.Length == 0 ? words[i] : line + " " + words[i];
            if (Fonts.Regular.MeasureString(next, Theme.TextSmall + 1) <= width || line.Length == 0) { line = next; continue; }
            if (lines.Count == maximum - 1) { lines.Add(Fit(line + " " + string.Join(" ", words, i, words.Length - i), width, Theme.TextSmall + 1, Fonts.Regular)); return lines.ToArray(); }
            lines.Add(Fit(line, width, Theme.TextSmall + 1, Fonts.Regular)); line = words[i];
        }
        if (line.Length > 0) { lines.Add(Fit(line, width, Theme.TextSmall + 1, Fonts.Regular)); }
        return lines.ToArray();
    }
}
