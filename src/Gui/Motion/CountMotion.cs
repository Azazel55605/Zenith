using System.Collections.Generic;
using System.Diagnostics;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Motion;

/// <summary>Badges and counts (docs/design/motion-additions-concept.svg, kernel 0.0.72): when a number the desktop shows
/// changes (the taskbar's +N, the sound level), the old one fades up and the new one rises in over 120 ms, and a ring
/// pulses once around a badge. The first value and Animations Off draw at once.</summary>
internal static class CountMotion
{
    private const long Micros = 120_000, RingMicros = 360_000;
    private const int Shift = 6;
    private sealed class State { public string Old = "", Now = ""; public long Start; }
    private static readonly Dictionary<int, State> s_states = new();
    private static long Clock => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;
    public static System.Func<int> AnimationPercent = () => 100;

    /// <summary>A count is still moving or its ring still fading: the desktop keeps drawing.</summary>
    public static bool Active
    {
        get
        {
            long now = Clock, ring = RingMicros * AnimationPercent() / 100;
            foreach (var s in s_states.Values) { if (s.Start != 0 && now - s.Start < ring) { return true; } }
            return false;
        }
    }

    /// <summary>Draws <paramref name="text"/> in <paramref name="box"/>, animating from what this key showed before.
    /// With <paramref name="ring"/>, a ring around it pulses once on a change.</summary>
    public static void Draw(Surface s, int key, string text, FontFace font, int size, uint color, Rect box, bool centered, Rect? ring = null)
    {
        if (!s_states.TryGetValue(key, out var state))
        {
            if (s_states.Count > 32) { s_states.Clear(); }
            state = new State { Now = text }; s_states[key] = state;
        }
        else if (state.Now != text)
        {
            state.Old = state.Now; state.Now = text;
            state.Start = AnimationPercent() > 0 ? Clock : 0;
        }
        long elapsed = state.Start == 0 ? long.MaxValue : Clock - state.Start;
        long duration = Micros * AnimationPercent() / 100, ringDuration = RingMicros * AnimationPercent() / 100;
        if (ring is Rect around && elapsed < ringDuration)
        {
            int k = MotionCurve.EaseOut(MotionCurve.Linear(elapsed, ringDuration));
            int grow = 6 * k / MotionCurve.One; uint alpha = (uint)(0x90 - 0x90 * k / MotionCurve.One);
            Rect r = around.Inflate(grow);
            s.StrokeRoundRect(r, System.Math.Min(r.W, r.H) / 2, (Theme.Accent & 0x00FFFFFFu) | alpha << 24);
        }
        if (elapsed >= duration)
        {
            if (elapsed >= ringDuration) { state.Start = 0; }
            Text(s, text, font, size, color, box, centered, 0); return;
        }
        int e = MotionCurve.EaseOut(MotionCurve.Linear(elapsed, duration)), one = MotionCurve.One;
        uint baseAlpha = color >> 24;
        uint fadeOut = (uint)(baseAlpha * (one - e) / one), fadeIn = (uint)(baseAlpha * e / one);
        Text(s, state.Old, font, size, (color & 0x00FFFFFFu) | fadeOut << 24, box, centered, -Shift * e / one);
        Text(s, text, font, size, (color & 0x00FFFFFFu) | fadeIn << 24, box, centered, Shift - Shift * e / one);
    }

    private static void Text(Surface s, string text, FontFace font, int size, uint color, Rect box, bool centered, int dy)
    {
        if (text.Length == 0 || (color >> 24) == 0) { return; }
        if (centered) { s.DrawTextCentered(text, font, size, color, box.Offset(0, dy), true); }
        else { s.DrawText(text, font, size, color, box.X, box.Y + dy); }
    }
}
