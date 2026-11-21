using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Zenith.Core.Notifications;

/// <summary>Notifications (U14, docs/design/motion-additions-concept.svg, kernel 0.0.72): programs and the system post
/// short messages; the desktop shows them as toasts that slide in from the edge. The sender's name always comes from
/// the kernel (the process name, or the system part that posted), never from the message, so a program cannot pose as
/// another. Bounded: titles 64 bytes, bodies 160, five posts per sender per ten seconds, the last 32 kept.</summary>
internal static class NotificationCenter
{
    public const int TitleBytes = 64, BodyBytes = 160, IconBytes = 32, Kept = 32, Burst = 5;
    public const long BurstMicros = 10_000_000;
    public const int ActionsMax = 2, ActionLabelBytes = 24, ActionArgumentBytes = 64;
    /// <summary>A button: opens <paramref name="App"/> with <paramref name="Args"/>, or (Restart) asks Sunset to restart.
    /// Programs may only name themselves; the system names any app.</summary>
    public sealed record NoteAction(string Label, string App, string[] Args, bool Restart = false);
    public sealed record Note(int Id, string Source, string Title, string Body, string Icon, uint Uid, long Posted, int SenderPid = 0, NoteAction[]? Actions = null);

    private static readonly object s_gate = new();
    private static readonly List<Note> s_history = new();
    private static readonly Queue<Note> s_new = new();
    private static readonly Dictionary<string, Queue<long>> s_recent = new();
    private static int s_next = 1, s_unread;
    private static readonly List<string> s_seen = new();
    /// <summary>Whether notifications from a sender are switched off (Meridian › Notifications); set at boot.</summary>
    public static Func<string, bool> IsMuted = _ => false;
    /// <summary>Every sender that posted since the system started, for the per-app switches.</summary>
    public static List<string> Senders() { lock (s_gate) { return new List<string>(s_seen); } }
    /// <summary>Posted since the history panel was last opened (the bell's count).</summary>
    public static int Unread { get { lock (s_gate) { return s_unread; } } }
    /// <summary>Changes whenever the history does (the taskbar redraws on it).</summary>
    public static int Revision { get; private set; }
    public static void MarkRead() { lock (s_gate) { s_unread = 0; Revision++; } }
    /// <summary>Removes one notification from the history (the panel's close mark).</summary>
    public static bool Remove(int id)
    {
        lock (s_gate)
        {
            int i = s_history.FindIndex(n => n.Id == id);
            if (i < 0) { return false; }
            s_history.RemoveAt(i); Revision++; return true;
        }
    }
    public static void Clear() { lock (s_gate) { s_history.Clear(); s_unread = 0; Revision++; } }
    /// <summary>Clock in microseconds; tests replace it.</summary>
    internal static Func<long> Clock = () => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;
    /// <summary>Raised (outside the lock) after a post is accepted: the desktop wakes for it.</summary>
    public static event Action? Posted;

    /// <summary>Posts a notification from <paramref name="source"/>; returns its id, 0 when the sender is switched off,
    /// -22 for an empty or oversized one, -16 when the sender posted too often.</summary>
    public static int Post(string source, string title, string body, string icon, uint uid, int pid = 0, NoteAction[]? actions = null)
    {
        title = Clean(title); body = Clean(body); icon = icon ?? "";
        if (title.Length == 0 || title.Length > TitleBytes || body.Length > BodyBytes || icon.Length > IconBytes || !ValidIcon(icon)) { return -22; }
        if (actions is not null)
        {
            if (actions.Length > ActionsMax) { return -22; }
            foreach (var a in actions)
            {
                if (Clean(a.Label).Length == 0 || a.Label.Length > ActionLabelBytes || a.Args.Length > 4) { return -22; }
                foreach (string arg in a.Args) { if (arg.Length > ActionArgumentBytes || Clean(arg) != arg) { return -22; } }
            }
        }
        if (string.IsNullOrEmpty(source)) { source = "System"; }
        Note note;
        lock (s_gate)
        {
            if (!s_seen.Contains(source) && s_seen.Count < 64) { s_seen.Add(source); }
        }
        if (IsMuted(source)) { Log.Write("notify", "muted " + source + ": " + title); return 0; }
        lock (s_gate)
        {
            long now = Clock();
            if (!s_recent.TryGetValue(source, out var recent)) { recent = new Queue<long>(); s_recent[source] = recent; }
            while (recent.Count > 0 && now - recent.Peek() >= BurstMicros) { recent.Dequeue(); }
            if (recent.Count >= Burst) { return -16; }
            recent.Enqueue(now);
            if (s_recent.Count > 64) { s_recent.Clear(); }
            note = new Note(s_next++, source, title, body, icon, uid, now, pid, actions);
            s_history.Add(note); if (s_history.Count > Kept) { s_history.RemoveAt(0); }
            s_unread = Math.Min(s_unread + 1, Kept); Revision++;
            s_new.Enqueue(note); while (s_new.Count > Kept) { s_new.Dequeue(); }
        }
        Log.Write("notify", "posted " + note.Id + " from " + source + ": " + title);
        Posted?.Invoke();
        return note.Id;
    }

    /// <summary>The system's own notifications (Orbit results, file jobs, the network, storage, sign-ins).</summary>
    public static void FromSystem(string source, string title, string body, string icon, params NoteAction[] actions)
    {
        try { Post(source, title, body, icon, 0, 0, actions.Length == 0 ? null : actions); } catch (Exception) { }
    }
    /// <summary>The button <paramref name="index"/> of notification <paramref name="id"/>, removing it from the history
    /// (a button finishes the notification); null when either is gone.</summary>
    public static NoteAction? Activate(int id, int index)
    {
        lock (s_gate)
        {
            int i = s_history.FindIndex(n => n.Id == id);
            var note = i >= 0 ? s_history[i] : null;
            if (note?.Actions is null || index < 0 || index >= note.Actions.Length) { return null; }
            s_history.RemoveAt(i); Revision++;
            Log.Write("notify", "action " + index + " of " + id + ": " + note.Actions[index].Label);
            return note.Actions[index];
        }
    }

    /// <summary>Notifications posted since the last call, oldest first (the desktop shows them).</summary>
    public static List<Note> TakeNew()
    {
        lock (s_gate) { var list = new List<Note>(s_new); s_new.Clear(); return list; }
    }
    public static List<Note> History() { lock (s_gate) { return new List<Note>(s_history); } }
    internal static void Reset() { lock (s_gate) { s_history.Clear(); s_new.Clear(); s_recent.Clear(); s_seen.Clear(); s_next = 1; s_unread = 0; Revision++; } IsMuted = _ => false; }

    // One line of plain text: control characters become spaces, the ends are trimmed.
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++) { if (chars[i] < ' ' || chars[i] == '\x7f') { chars[i] = ' '; } }
        return new string(chars).Trim();
    }
    private static bool ValidIcon(string icon)
    {
        foreach (char c in icon) { if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) { return false; } }
        return true;
    }
}
