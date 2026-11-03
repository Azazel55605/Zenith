using System;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

/// <summary>Opt-in C3 diagnostic, opened and painted only by the UI owner.</summary>
internal sealed class CpuFramePreview : Window
{
    private readonly uint[] _pixels;
    private readonly string _timing;

    internal CpuFramePreview(uint[] pixels, int cpus, long micros) : base("Managed CPU frame", 420, 260)
    {
        if (pixels.Length != 128 * 128) { throw new ArgumentException("frame dimensions"); }
        _pixels = pixels;
        _timing = cpus + " CPU(s), " + micros + " us";
    }

    public override void OnKey(KeyEvent key)
    {
        if (key.Key == ConsoleKeyEx.Escape)
        {
            Close();Zenith.Core.Log.Write("cpu", "managed frame preview closed");
        }
        else { Zenith.Core.Log.Write("cpu", "managed frame preview input acknowledged"); }
    }

    public override bool CanResize => false;
    public override bool AllowBackgroundMaterials => true;

    public override void DrawContent(Surface surface, Rect content)
    {
        PaintWorkspace(surface, content, 0);
        int x = content.X + 24, y = content.Y + 24;
        Rect clip = new Rect(x, y, 128, 128).Intersect(surface.Clip);
        for (int row = clip.Y; row < clip.Bottom; row++)
        {
            for (int column = clip.X; column < clip.Right; column++)
            {
                surface.Pixels[row * surface.Width + column] = (int)_pixels[(row - y) * 128 + column - x];
            }
        }
        surface.DrawText("Complete owned frame", Fonts.Regular, Theme.TextBody, Theme.TextPrimary, x + 148, y);
        surface.DrawText(_timing, Fonts.Regular, Theme.TextBody, Theme.TextSecondary, x + 148, y + 28);
        surface.DrawText("AP compute, BSP publication", Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, y + 152);
    }
}
