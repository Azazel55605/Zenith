using System;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Motion;

internal enum MotionEnd { None, Finished, OverBudget, Interrupted }

/// <summary>
/// Plays one layer over a fixed base image. Times are in microseconds. The real window state has
/// already changed when this runs, so ending early only skips to what the next scene shows.
/// </summary>
internal sealed class MotionPlayer
{
    public const long BudgetMicros = 33_333;
    private const int SlowFramesAllowed = 2;

    private int[] _base = Array.Empty<int>();
    private PixelLayer? _layer;
    private Rect _from, _to;
    private int _fromAlpha, _toAlpha, _slowFrames;
    private long _start, _duration;

    public bool Active { get; private set; }
    public MotionEnd LastEnd { get; private set; }
    public int Frames { get; private set; }
    public long WorstFrameMicros { get; private set; }
    public long ElapsedMicros { get; private set; }

    public void Begin(int[] baseImage, PixelLayer layer, Rect from, Rect to, int fromAlpha, int toAlpha, long now, long duration)
    {
        _base = baseImage; _layer = layer; _from = from; _to = to; _fromAlpha = fromAlpha; _toAlpha = toAlpha;
        _start = now; _duration = duration; _slowFrames = 0;
        Frames = 0; WorstFrameMicros = 0; ElapsedMicros = 0; LastEnd = MotionEnd.None;
        Active = duration > 0;
        if (!Active) { LastEnd = MotionEnd.Finished; }
    }

    /// <summary>Draws the frame for <paramref name="now"/>; false when nothing was drawn because the animation is over.</summary>
    public bool Render(int[] frame, int width, int height, long now)
    {
        if (!Active) { return false; }
        ElapsedMicros = now - _start;
        if (ElapsedMicros >= _duration) { End(MotionEnd.Finished); return false; }
        int p = MotionCurve.EaseOut(MotionCurve.Linear(ElapsedMicros, _duration));
        Array.Copy(_base, frame, frame.Length);
        _layer!.Draw(frame, width, height,
            new Rect(MotionCurve.Lerp(_from.X, _to.X, p), MotionCurve.Lerp(_from.Y, _to.Y, p),
                     MotionCurve.Lerp(_from.W, _to.W, p), MotionCurve.Lerp(_from.H, _to.H, p)),
            MotionCurve.Lerp(_fromAlpha, _toAlpha, p));
        return true;
    }

    /// <summary>Reports how long a presented frame took; repeated slow frames end the animation.</summary>
    public void RecordFrame(long micros)
    {
        Frames++;
        WorstFrameMicros = Math.Max(WorstFrameMicros, micros);
        _slowFrames = micros > BudgetMicros ? _slowFrames + 1 : 0;
        if (_slowFrames >= SlowFramesAllowed) { End(MotionEnd.OverBudget); }
    }

    public void Interrupt()
    {
        if (Active) { End(MotionEnd.Interrupted); }
    }

    private void End(MotionEnd end)
    {
        Active = false; LastEnd = end; _layer = null; _base = Array.Empty<int>();
    }
}
