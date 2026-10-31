using System;
using Zenith.Core.Processes;

namespace Zenith.Gui.Graphics;

/// <summary>Host-only, bounded software materials. Captures the lower scene before
/// painting the window, never after/including its foreground. No client readback.</summary>
internal sealed class MaterialComposition
{
    private const int BlurRadius = 4;
    private readonly int _sampleScale;
    public static int SampleLength(int length, int scale) => (length + scale - 1) / scale;
    private readonly int[] _source;
    private readonly int[] _scratch;
    private readonly int[] _filtered;
    private readonly ProcessMaterialRegion[] _previous = new ProcessMaterialRegion[ProcessMaterialRegion.MaximumRegions];
    public MaterialComposition(int capacity = ProcessMaterialRegion.MaximumArea, bool blur = true, int sampleScale = 1)
    { _sampleScale = sampleScale is 1 or 4 ? sampleScale : throw new ArgumentOutOfRangeException(nameof(sampleScale)); _source = new int[capacity]; _scratch = blur ? new int[capacity] : Array.Empty<int>(); _filtered = blur ? new int[capacity] : _source; }
    private int _count = -1;
    private int[] _row = Array.Empty<int>(), _coarse = Array.Empty<int>();
    public int BlurRebuilds { get; private set; }

    // Finished material pixels before corner coverage, kept once a capture repeats. Repainting
    // them is a row copy; the bilinear reconstruction costs several blends for every pixel.
    private int[]? _painted;
    private bool _paintedValid, _stable;
    private int _paintedBytes;
    private uint _paintedFallback, _paintedPanel, _paintedNavigation, _paintedPopover;
    public bool HasPaintedCache => _paintedValid;

    public void ReleasePainted()
    {
        MaterialResources.ReleasePainted(_paintedBytes);
        _paintedBytes = 0; _painted = null; _paintedValid = false;
    }

    /// <param name="backdropUnchanged">The caller knows the pixels under these regions match the last capture.</param>
    public void Capture(int[] scene, int width, int height, Rect content, ProcessMaterialRegion[] regions, int count, uint fallback, bool backdropUnchanged = false)
    {
        bool changed = _count != count; int offset = 0;
        for (int i = 0; i < count; i++) { changed |= _previous[i] != regions[i]; }
        if (backdropUnchanged && !changed) { _stable = true; return; }
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; _previous[i] = r;
            int sampledWidth = SampleLength(r.Width, _sampleScale), sampledHeight = SampleLength(r.Height, _sampleScale);
            if (r.Effect == 0) { offset += sampledWidth * sampledHeight; continue; }
            for (int y = 0; y < sampledHeight; y++)
            {
                for (int x = 0; x < sampledWidth; x++)
                {
                    int red = 0, green = 0, blue = 0, samples = 0;
                    for (int dy = 0; dy < _sampleScale && y * _sampleScale + dy < r.Height; dy++)
                    for (int dx = 0; dx < _sampleScale && x * _sampleScale + dx < r.Width; dx++)
                    {
                        int sx = content.X + r.X + x * _sampleScale + dx, sy = content.Y + r.Y + y * _sampleScale + dy;
                        int color = sx < 0 || sy < 0 || sx >= width || sy >= height ? (int)fallback : scene[sy * width + sx];
                        red += (color >> 16) & 255; green += (color >> 8) & 255; blue += color & 255; samples++;
                    }
                    int colorAverage = unchecked((int)0xff000000) | (red / samples << 16) | (green / samples << 8) | blue / samples;
                    int slot = offset + y * sampledWidth + x; changed |= _source[slot] != colorAverage; _source[slot] = colorAverage;
                }
            }
            offset += sampledWidth * sampledHeight;
        }
        _count = count;
        _stable = !changed;
        if (!changed) { return; }
        ReleasePainted();
        offset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; int sampledWidth = SampleLength(r.Width, _sampleScale), sampledHeight = SampleLength(r.Height, _sampleScale);
            int area = sampledWidth * sampledHeight;
            if (r.Effect == 2)
            {
                Blur(_source, _scratch, offset, sampledWidth, sampledHeight, true);
                Blur(_scratch, _filtered, offset, sampledWidth, sampledHeight, false); BlurRebuilds++;
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

    // One finished row of a region, before corner coverage. Bilinear reconstruction avoids visible
    // low-resolution cells; the tint is linear too, so it is blended at the coarse size.
    private void MaterialRow(int offset, ProcessMaterialRegion r, int py, uint tint, int opacity, int[] target, int at)
    {
        int sw = SampleLength(r.Width, _sampleScale);
        if (_sampleScale == 1) { MaterialRows.Tint(_filtered, offset + py * sw, target, at, r.Width, tint, opacity); return; }
        int sh = SampleLength(r.Height, _sampleScale), sy = py / _sampleScale, nextY = Math.Min(sy + 1, sh - 1);
        if (_coarse.Length < sw) { _coarse = new int[sw]; }
        MaterialRows.Mix(_filtered, offset + sy * sw, _filtered, offset + nextY * sw, _coarse, sw, py % _sampleScale);
        MaterialRows.Tint(_coarse, 0, _coarse, 0, sw, tint, opacity);
        MaterialRows.Expand(_coarse, sw, target, at, r.Width);
    }

    public void Paint(int[] target, int width, int height, Rect content,
        ProcessMaterialRegion[] regions, int count, uint fallback, uint panel, uint navigation, uint popover, bool clear)
    {
        Rect clip = content.Intersect(new Rect(0, 0, width, height));
        if (clear)for (int y = clip.Y; y < clip.Bottom; y++)
        { Array.Fill(target, (int)(fallback | 0xff000000U), y * width + clip.X, clip.W); }
        int[]? painted = _stable ? Painted(regions, count, fallback, panel, navigation, popover) : null;
        int offset = 0, paintedOffset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; uint tint = r.Role == 1 ? fallback : r.Role == 3 ? navigation : r.Role == 5 ? popover : panel;
            Rect rect = new(content.X + r.X, content.Y + r.Y, r.Width, r.Height);
            Rect visible = rect.Intersect(clip);
            int opacity = r.Opacity > 0 ? r.Opacity : r.Effect == 1 ? 220 : 210;
            bool cached = painted is not null && r.Effect != 0;
            if (!cached && _row.Length < r.Width) { _row = new int[r.Width]; }
            if (r.Effect == 0 && visible.H > 0) { Array.Fill(_row, (int)(tint | 0xff000000U), 0, r.Width); }
            for (int y = visible.Y; y < visible.Bottom; y++)
            {
                int py = y - rect.Y, at = y * width;
                if (!cached && r.Effect != 0) { MaterialRow(offset, r, py, tint, opacity, _row, 0); }
                int[] pixels = cached ? painted! : _row;
                int source = (cached ? paintedOffset + py * r.Width : 0) - rect.X;
                int cornerRow = r.Radius > 0 && Math.Min(py, r.Height - 1 - py) < r.Radius ? r.Radius : 0;
                int solidStart = Math.Min(visible.Right, Math.Max(visible.X, rect.X + cornerRow));
                int solidEnd = Math.Max(solidStart, Math.Min(visible.Right, rect.Right - cornerRow));
                for (int x = visible.X; x < solidStart; x++) { PaintCorner(target, at + x, pixels[source + x], x - rect.X, py, r); }
                Array.Copy(pixels, source + solidStart, target, at + solidStart, solidEnd - solidStart);
                for (int x = solidEnd; x < visible.Right; x++) { PaintCorner(target, at + x, pixels[source + x], x - rect.X, py, r); }
            }
            if (cached) { paintedOffset += r.Width * r.Height; }
            offset += SampleLength(r.Width, _sampleScale) * SampleLength(r.Height, _sampleScale);
        }
    }

    private static void PaintCorner(int[] target, int at, int color, int px, int py, ProcessMaterialRegion r)
    {
        int dx = Math.Min(px, r.Width - 1 - px), dy = Math.Min(py, r.Height - 1 - py);
        int coverage = dx < r.Radius && dy < r.Radius ? RoundedComposition.Coverage(dx, dy, r.Radius) : 255;
        if (coverage == 255) { target[at] = color; }
        else if (coverage != 0) { target[at] = RoundedComposition.Blend(color, target[at], coverage); }
    }

    /// <summary>The finished pixels for these regions, or null when they do not fit the shared budget.</summary>
    private int[]? Painted(ProcessMaterialRegion[] regions, int count, uint fallback, uint panel, uint navigation, uint popover)
    {
        if (_paintedValid && fallback == _paintedFallback && panel == _paintedPanel && navigation == _paintedNavigation && popover == _paintedPopover)
        { return _painted; }
        long pixels = 0;
        for (int i = 0; i < count; i++) { if (regions[i].Effect != 0) { pixels += (long)regions[i].Width * regions[i].Height; } }
        if (pixels == 0 || pixels > MaterialResources.MaximumPaintedBytes / sizeof(int)) { return null; }
        if (_painted is null || _painted.Length != pixels)
        {
            ReleasePainted();
            if (!MaterialResources.ReservePainted((int)pixels * sizeof(int))) { return null; }
            _paintedBytes = (int)pixels * sizeof(int); _painted = new int[pixels];
        }
        int offset = 0, at = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; uint tint = r.Role == 1 ? fallback : r.Role == 3 ? navigation : r.Role == 5 ? popover : panel;
            if (r.Effect != 0)
            {
                int opacity = r.Opacity > 0 ? r.Opacity : r.Effect == 1 ? 220 : 210;
                for (int py = 0; py < r.Height; py++) { MaterialRow(offset, r, py, tint, opacity, _painted, at); at += r.Width; }
            }
            offset += SampleLength(r.Width, _sampleScale) * SampleLength(r.Height, _sampleScale);
        }
        _paintedFallback = fallback; _paintedPanel = panel; _paintedNavigation = navigation; _paintedPopover = popover; _paintedValid = true;
        return _painted;
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
