using System;
using System.Collections.Generic;

namespace Zenith.Core.Sessions;

/// <summary>The four ways to leave a session, in the order Sunset shows them.</summary>
internal enum SunsetAction { Lock, LogOut, Restart, ShutDown }

/// <summary>What would be lost or interrupted: the program or window (<paramref name="Owner"/>), why, the
/// kind of thing (an unsaved file or a running task) and the window that can deal with it, if any.</summary>
internal sealed record SessionBlocker(string Owner, string Reason, bool Unsaved, object? Target);

/// <summary>One button: its action, label, the key that chooses it and why it cannot be used (or null).</summary>
internal sealed record SunsetChoice(SunsetAction Action, string Label, char Key, string Icon, string? Disabled);

/// <summary>Sunset's rules, apart from drawing (docs/components/SUNSET.md). Lock interrupts nothing, so only a
/// missing password stops it; log out, restart and shut down wait until nothing would be lost.</summary>
internal static class SunsetPlan
{
    public const int MaximumBlockers = 6;

    public static SunsetChoice[] Choices(IReadOnlyList<SessionBlocker> blockers, string? lockUnavailable, string? logoutUnavailable, string? powerUnavailable)
    {
        string? blocked = blockers.Count == 0 ? null : "Deal with the items below first.";
        return new[]
        {
            new SunsetChoice(SunsetAction.Lock, "Lock", 'L', "lock", lockUnavailable),
            new SunsetChoice(SunsetAction.LogOut, "Log out", 'O', "log-out", logoutUnavailable ?? blocked),
            new SunsetChoice(SunsetAction.Restart, "Restart", 'R', "rotate-ccw", powerUnavailable ?? blocked),
            new SunsetChoice(SunsetAction.ShutDown, "Shut down", 'S', "power", powerUnavailable ?? blocked),
        };
    }

    /// <summary>The line under the buttons.</summary>
    public static string Summary(int blockers) => blockers switch
    {
        0 => "Everything is saved. Nothing is running.",
        1 => "One thing would be lost or interrupted",
        _ => Number(blockers) + " things would be lost or interrupted",
    };

    private static string Number(int n) => n switch
    {
        2 => "Two", 3 => "Three", 4 => "Four", 5 => "Five", 6 => "Six", 7 => "Seven", 8 => "Eight", 9 => "Nine",
        _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>"up 3 h 12 min", "up 12 min", "up 2 d 4 h", "up 40 s".</summary>
    public static string Uptime(long seconds)
    {
        if (seconds < 0) { seconds = 0; }
        long days = seconds / 86400, hours = seconds / 3600 % 24, minutes = seconds / 60 % 60;
        if (days > 0) { return "up " + days + " d" + (hours > 0 ? " " + hours + " h" : ""); }
        if (hours > 0) { return "up " + hours + " h" + (minutes > 0 ? " " + minutes + " min" : ""); }
        if (minutes > 0) { return "up " + minutes + " min"; }
        return "up " + seconds + " s";
    }

    /// <summary>The choice for a typed letter (L, O, R, S in either case), or -1.</summary>
    public static int ForKey(IReadOnlyList<SunsetChoice> choices, char key)
    {
        char upper = char.ToUpperInvariant(key);
        for (int i = 0; i < choices.Count; i++) { if (choices[i].Key == upper) { return i; } }
        return -1;
    }

    /// <summary>The next usable choice from <paramref name="focus"/> in a direction, or focus when none is.</summary>
    public static int Move(IReadOnlyList<SunsetChoice> choices, int focus, int step)
    {
        for (int i = 1; i <= choices.Count; i++)
        {
            int next = ((focus + step * i) % choices.Count + choices.Count) % choices.Count;
            if (choices[next].Disabled is null) { return next; }
        }
        return focus;
    }

    /// <summary>Where focus starts: the asked-for action when it can be used, else Lock (it loses nothing), else
    /// the first usable one.</summary>
    public static int Initial(IReadOnlyList<SunsetChoice> choices, SunsetAction preferred)
    {
        for (int i = 0; i < choices.Count; i++) { if (choices[i].Action == preferred && choices[i].Disabled is null) { return i; } }
        if (choices[0].Disabled is null) { return 0; }
        return Move(choices, 0, 1);
    }
}

/// <summary>The lock screen's attempt policy. Each wrong password costs the second the prompt already waits;
/// from the fifth in a row, each further try waits 30 seconds more (at most five minutes).</summary>
internal sealed class ScreenLockPolicy
{
    public const int FreeAttempts = 4;
    public const long StepSeconds = 30, MaximumSeconds = 300;
    private long _retryAt;
    public int Failures { get; private set; }

    public void Failed(long nowSeconds)
    {
        Failures++;
        if (Failures > FreeAttempts) { _retryAt = nowSeconds + Math.Min(MaximumSeconds, (Failures - FreeAttempts) * StepSeconds); }
    }

    public void Succeeded() { Failures = 0; _retryAt = 0; }

    /// <summary>Seconds to wait before the next attempt; 0 when one may be made now.</summary>
    public long Wait(long nowSeconds) => Math.Max(0, _retryAt - nowSeconds);
}

/// <summary>Monotonic inactivity deadline for one desktop session. A late poll retains the
/// original due time so a busy frame cannot grant extra password-free wake time.</summary>
internal sealed class IdleLockTimer
{
    private long _lastActivity;
    private int _timeout = -1;

    public long? Poll(long now, int timeout, bool activity)
    {
        if (timeout != _timeout || now < _lastActivity) { _timeout = timeout; _lastActivity = now; }
        if (timeout > 0 && now - _lastActivity >= timeout)
        {
            long due = _lastActivity + timeout;
            _lastActivity = now;
            return due;
        }
        if (activity || timeout == 0) { _lastActivity = now; }
        return null;
    }

    public void Reset() => _timeout = -1;
    public static bool CanWake(long now, long graceUntil) => graceUntil > 0 && now < graceUntil;
}
