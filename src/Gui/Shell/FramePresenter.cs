using System;
using Cosmos.Kernel.System.Graphics;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>
/// Puts a logical frame on the screen: only the damaged rectangle is copied (every driver copies
/// rectangles row by row). Integer scale expands each row once and copies it to the other scale
/// rows instead of writing pixel by pixel. Drivers read width*height pixels at stride width.
/// </summary>
internal sealed class FramePresenter
{
    private int[] _buffer = Array.Empty<int>();

    public void Present(Canvas screen, int[] frame, int width, int height, Rect damage)
    {
        damage = damage.Intersect(new Rect(0, 0, width, height));
        if (damage.W <= 0 || damage.H <= 0) { return; }
        Canvas target = screen is DisplayCanvas scaled ? scaled.Physical : screen;
        int scale = screen is DisplayCanvas s ? s.Scale : 1;
        if (scale == 1 && damage.X == 0 && damage.Y == 0 && damage.W == width && damage.H == height)
        {
            target.DrawArray(frame, 0, 0, width, height);
            target.Display();
            return;
        }
        int pw = damage.W * scale, ph = damage.H * scale;
        if (_buffer.Length < pw * ph) { _buffer = new int[pw * ph]; }
        Span<int> output = _buffer;
        for (int row = 0; row < damage.H; row++)
        {
            ReadOnlySpan<int> source = frame.AsSpan((damage.Y + row) * width + damage.X, damage.W);
            Span<int> first = output.Slice(row * scale * pw, pw);
            if (scale == 1) { source.CopyTo(first); continue; }
            for (int x = 0, o = 0; x < source.Length; x++)
            {
                int v = source[x];
                for (int d = 0; d < scale; d++) { first[o++] = v; }
            }
            for (int d = 1; d < scale; d++) { first.CopyTo(output.Slice((row * scale + d) * pw, pw)); }
        }
        target.DrawArray(_buffer, damage.X * scale, damage.Y * scale, pw, ph);
        target.Display();
    }
}
