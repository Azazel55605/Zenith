using System;
using System.Collections.Generic;
using System.Diagnostics;
using Zenith.Core.Notifications;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>Notification toasts (docs/design/motion-additions-concept.svg, kernel 0.0.72): up to three cards stacked
/// above the taskbar at the right edge. Each slides in from the edge (180 ms), stays its popup time (Meridian ›
/// Notifications; the pointer over it holds it) and slides out when it ends or is clicked. Do Not Disturb keeps them
/// in the history without a card or a sound. The sender line comes from the kernel, so a program cannot pose as another.</summary>
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
    }
    private readonly List<Card> _cards = new();
    private readonly Queue<NotificationCenter.Note> _waiting = new();
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
    public bool Tick(Rect work, int mouseX, int mouseY, bool historyOpen = false)
    {
        var settings = Zenith.Core.Configuration.SystemConfiguration.Settings;
        bool dnd = (settings?.DoNotDisturb ?? false) || historyOpen, changed = false;   // the open history shows them itself
        if (historyOpen) { foreach (var c in _cards) { if (c.Leaving == 0) { c.Leaving = Now; changed = true; } } _waiting.Clear(); }
        foreach (var note in NotificationCenter.TakeNew())
        {
            if (dnd) { continue; }   // kept in the history only
            _waiting.Enqueue(note); while (_waiting.Count > 8) { _waiting.Dequeue(); }
        }
        long now = Now, slide = SlideMicros * AnimationPercent() / 100;
        long hold = (long)Zenith.Core.Configuration.SettingsService.PopupSeconds[Math.Min(2u, settings?.Popup ?? 1)] * 1_000_000;
        bool sounded = false;
        while (_cards.Count < MaxShown && _waiting.Count > 0)
        {
            var note = _waiting.Dequeue();
            _cards.Insert(0, new Card { Note = note, Shown = now, Deadline = now + hold });
            if (!sounded) { sounded = true; Zenith.Core.Audio.SystemSounds.Play("notification"); }
            Zenith.Core.Log.Write("notify", "toast " + note.Id + " shown");
            changed = true;
        }
        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            var c = _cards[i];
            if (c.Leaving == 0)
            {
                if (c.Bounds.Contains(mouseX, mouseY)) { c.Deadline = Math.Max(c.Deadline, now + HoldAfterHoverMicros); }
                else if (now >= c.Deadline) { c.Leaving = now; changed = true; }
            }
            else if (now - c.Leaving >= slide) { _cards.RemoveAt(i); changed = true; }
        }
        Layout(work);
        return changed || Moving;
    }

    private void Layout(Rect work)
    {
        int width = Math.Min(Width, work.W - 2 * Margin), bottom = work.Bottom - Margin, inner = width - 2 * Pad - 44;
        foreach (var c in _cards)
        {
            if (c.Lines.Length == 0) { c.Lines = Wrap(c.Note.Body, inner, 2); }
            int height = Pad + 16 + 20 + c.Lines.Length * 18 + Pad;
            c.Bounds = new Rect(work.Right - Margin - width, bottom - height, width, height);
            bottom -= height + Gap;
        }
    }

    /// <summary>How far right of its place a card is drawn now: sliding in from beyond the edge, or out again.</summary>
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

    public bool Contains(int x, int y)
    {
        foreach (var c in _cards) { if (c.Leaving == 0 && c.Bounds.Contains(x, y)) { return true; } }
        return false;
    }

    /// <summary>A click on a card dismisses it (sliding out). True when the click landed on one.</summary>
    public bool HandleClick(int x, int y)
    {
        foreach (var c in _cards)
        {
            if (c.Leaving == 0 && c.Bounds.Contains(x, y))
            {
                c.Leaving = Now; Zenith.Core.Log.Write("notify", "toast " + c.Note.Id + " dismissed");
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
            Rect r = c.Bounds.Offset(Offset(c, now), 0);
            if (r.X >= s.Width) { continue; }
            s.DrawShadow(r, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
            s.FillRoundRect(r, Theme.WindowRadius, Theme.WindowBackground | 0xFF000000u);
            s.StrokeRoundRect(r, Theme.WindowRadius, Theme.WindowBorder);
            Rect badge = new(r.X + Pad, r.Y + Pad, 32, 32);
            s.FillRoundRect(badge, 9, Theme.AccentSoft);
            string icon = c.Note.Icon.Length > 0 ? c.Note.Icon : "info";
            // Without the icon package: the Zenith mark, as the permission dialog does.
            if (!Icons.Draw(s, icon, badge.X + 6, badge.Y + 6, 20, Theme.Accent) && !Icons.Draw(s, "info", badge.X + 6, badge.Y + 6, 20, Theme.Accent))
            { Logo.Draw(s, new Rect(badge.X + 6, badge.Y + 6, 20, 20)); }
            int x = badge.Right + 12, right = r.Right - Pad - (c.Bounds.Contains(mouseX, mouseY) ? 24 : 0);
            s.DrawText(Fit(c.Note.Source, right - x, Theme.TextSmall, Fonts.Regular), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, r.Y + Pad - 2);
            s.DrawText(Fit(c.Note.Title, right - x, Theme.TextBody, Fonts.SemiBold), Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, x, r.Y + Pad + 14);
            int y = r.Y + Pad + 36;
            foreach (string line in c.Lines) { s.DrawText(line, Fonts.Regular, Theme.TextSmall + 1, Theme.TextSecondary, x, y); y += 18; }
            // The close mark shows under the pointer; a click anywhere on the card dismisses it.
            if (c.Leaving == 0 && c.Bounds.Contains(mouseX, mouseY) && !Icons.Draw(s, "x", r.Right - Pad - 16, r.Y + Pad, 16, Theme.TextSecondary))
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
