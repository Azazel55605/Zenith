using System;
using Zenith.Core.Processes;

namespace Zenith.Gui.Graphics;

/// <summary>Host-only, bounded software materials. Captures the lower scene before
/// painting the window, never after/including its foreground. No client readback.</summary>
internal sealed class MaterialComposition
{
    private const int BlurRadius = 4;
    private readonly int[] _source;
    private readonly int[] _scratch;
    private readonly int[] _filtered;
    private readonly ProcessMaterialRegion[] _previous = new ProcessMaterialRegion[ProcessMaterialRegion.MaximumRegions];
    public MaterialComposition(int capacity = ProcessMaterialRegion.MaximumArea, bool blur = true)
    { _source = new int[capacity]; _scratch = blur ? new int[capacity] : Array.Empty<int>(); _filtered = blur ? new int[capacity] : _source; }
    private int _count = -1;
    public int BlurRebuilds { get; private set; }

    public void Capture(int[] scene, int width, int height, Rect content, ProcessMaterialRegion[] regions, int count, uint fallback)
    {
        bool changed = _count != count; int offset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; changed |= _previous[i] != r; _previous[i] = r;
            if (r.Effect == 0) { offset += r.Width * r.Height;continue; }
            for (int y = 0; y < r.Height; y++)
            {
                for (int x = 0; x < r.Width; x++)
                {
                    int sx = content.X + r.X + x, sy = content.Y + r.Y + y;
                    int color = sx < 0 || sy < 0 || sx >= width || sy >= height ? (int)fallback : scene[sy * width + sx];
                    int slot = offset + y * r.Width + x; changed |= _source[slot] != color; _source[slot] = color;
                }
            }
            offset += r.Width * r.Height;
        }
        _count = count;
        if (!changed) { return; }
        offset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; int area = r.Width * r.Height;
            if (r.Effect == 2)
            {
                Blur(_source, _scratch, offset, r.Width, r.Height, true);
                Blur(_scratch, _filtered, offset, r.Width, r.Height, false); BlurRebuilds++;
            }
            else if (r.Effect != 0) { Array.Copy(_source, offset, _filtered, offset, area); }
            offset += area;
        }
    }

    // Separable box diffusion, O(area), clamped to this region's captured rectangle.
    // Client sizes/radii never become loop budgets or arbitrary sampling offsets.
    private static void Blur(int[] source, int[] target, int offset, int width, int height, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        for (int line = 0; line < lines; line++)
        {
            int start = offset + (horizontal ? line * width : line), step = horizontal ? 1 : width;
            int red = 0, green = 0, blue = 0;
            for (int k = -BlurRadius; k <= BlurRadius; k++)
            {
                int color = source[start + Math.Clamp(k, 0, length - 1) * step];
                red += (color >> 16) & 255; green += (color >> 8) & 255; blue += color & 255;
            }
            for (int p = 0; p < length; p++)
            {
                target[start + p * step] = unchecked((int)0xff000000) | ((red + 4) / 9 << 16) | ((green + 4) / 9 << 8) | (blue + 4) / 9;
                int remove = source[start + Math.Clamp(p - BlurRadius, 0, length - 1) * step];
                int add = source[start + Math.Clamp(p + BlurRadius + 1, 0, length - 1) * step];
                red += ((add >> 16) & 255) - ((remove >> 16) & 255);
                green += ((add >> 8) & 255) - ((remove >> 8) & 255); blue += (add & 255) - (remove & 255);
            }
        }
    }

    public void Paint(int[] target, int width, int height, Rect content,
        ProcessMaterialRegion[] regions, int count, uint fallback, uint panel, uint navigation, uint popover, bool clear)
    {
        Rect clip = content.Intersect(new Rect(0, 0, width, height));
        if (clear)for (int y = clip.Y; y < clip.Bottom; y++)
        { Array.Fill(target, (int)(fallback | 0xff000000U), y * width + clip.X, clip.W); }
        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; uint tint = r.Role == 1 ? fallback : r.Role == 3 ? navigation : r.Role == 5 ? popover : panel;
            Rect rect = new(content.X + r.X, content.Y + r.Y, r.Width, r.Height);
            Rect visible = rect.Intersect(clip);
            for (int y = visible.Y; y < visible.Bottom; y++)
            {
                for (int x = visible.X; x < visible.Right; x++)
                {
                    int px = x - rect.X, py = y - rect.Y;
                    int dx = Math.Min(px, r.Width - 1 - px), dy = Math.Min(py, r.Height - 1 - py);
                    int coverage = dx < r.Radius && dy < r.Radius ? RoundedComposition.Coverage(dx, dy, r.Radius) : 255;
                    int color = r.Effect == 0 ? (int)(tint | 0xff000000U)
                        : RoundedComposition.Blend((int)tint, _filtered[offset + py * r.Width + px], r.Opacity > 0 ? r.Opacity : r.Effect == 1 ? 220 : 210);
                    if (coverage == 255) { target[y * width + x] = color; }
                    else if (coverage != 0) { target[y * width + x] = RoundedComposition.Blend(color, target[y * width + x], coverage); }
                }
            }
            offset += r.Width * r.Height;
        }
    }
    public void Compose(int[] frame, int[] target, int width, int height, Rect content,
        ProcessMaterialRegion[] regions, int count, uint fallback, uint panel, uint navigation, uint popover)
    {
        Paint(target, width, height, content, regions, count, fallback, panel, navigation, popover, true);
        Rect clip = content.Intersect(new Rect(0, 0, width, height));
        for (int y = clip.Y; y < clip.Bottom; y++)
        {
            for (int x = clip.X; x < clip.Right; x++)
            {
                int fg = frame[(y - content.Y) * content.W + x - content.X], at = y * width + x;
                int alpha = (int)((uint)fg >> 24);
                if (alpha == 255) { target[at] = fg; continue; }
                if (alpha == 0) { continue; }
                int bg = target[at]; uint color = 0xff000000;
                for (int shift = 0; shift < 24; shift += 8)
                { color |= (uint)(((fg >> shift) & 255) + (((bg >> shift) & 255) * (255 - alpha) + 127) / 255) << shift; }
                target[at] = (int)color;
            }
        }
    }
}
