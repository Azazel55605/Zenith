using System;
using Zenith.Core.Processes;
using Zenith.Core.Multiprocessing;

namespace Zenith.Gui.Graphics;

/// <summary>Host-only, bounded software materials. Captures the lower scene before
/// painting the window, never after/including its foreground. No client readback.</summary>
internal sealed class MaterialComposition
{
    private readonly int _sampleScale;
    public static int SampleLength(int length, int scale) => (length + scale - 1) / scale;
    private readonly int[] _source;
    private readonly int[] _scratch;
    private readonly int[] _filtered;
    private readonly ProcessMaterialRegion[] _previous = new ProcessMaterialRegion[ProcessMaterialRegion.MaximumRegions];
    public MaterialComposition(int capacity = ProcessMaterialRegion.MaximumArea, bool blur = true, int sampleScale = 1)
    { _sampleScale = sampleScale is 1 or 4 ? sampleScale : throw new ArgumentOutOfRangeException(nameof(sampleScale)); _source = new int[capacity]; _scratch = blur ? new int[capacity] : Array.Empty<int>(); _filtered = blur ? new int[capacity] : _source; }
    private int _count = -1;
    private int _blurRevision = -1;
    // The blurred backdrop's luminance range per region, which decides how much of it may show.
    private readonly double[] _low = new double[ProcessMaterialRegion.MaximumRegions], _high = new double[ProcessMaterialRegion.MaximumRegions];
    private int OpacityFor(int index, ProcessMaterialRegion r, uint tint)
        => r.Opacity > 0 ? MaterialPolicy.AdaptedOpacity(r.Opacity, tint, Theme.TextPrimary, Theme.TextSecondary, r.Effect,
            MaterialResources.Policy.Translucency, _low[index], _high[index]) : r.Effect == 1 ? 220 : 210;
    private int[] _row = Array.Empty<int>(), _coarse = Array.Empty<int>();
    public int BlurRebuilds { get; private set; }

    // Finished material pixels before corner coverage, kept once a capture repeats. Repainting
    // them is a row copy; the bilinear reconstruction costs several blends for every pixel.
    private int[]? _painted;
    private bool _paintedValid, _stable;
    private int _paintedBytes;
    private uint _paintedFallback, _paintedPanel, _paintedNavigation, _paintedPopover;
    public bool HasPaintedCache => _paintedValid;
    /// <summary>One averaged backdrop sample (tests).</summary>
    internal int SampleAt(int index) => _source[index];
    /// <summary>Painting and composing change only pixels inside this (a partial compose's damaged
    /// rectangle; the compositor is the only, single-threaded caller). Captures are not limited.</summary>
    public static readonly Rect Unlimited = new(0, 0, int.MaxValue / 2, int.MaxValue / 2);
    public static Rect Limit { get; set; } = Unlimited;

    public void ReleasePainted()
    {
        MaterialResources.ReleasePainted(_paintedBytes);
        _paintedBytes = 0; _painted = null; _paintedValid = false;
    }

    /// <summary>Set by the window manager around one window's captures: the only screen pixels under it
    /// that may have changed since its last capture. Empty means unknown (sample everything).</summary>
    public static Rect Dirty { get; set; }
    /// <summary>How far outside the changed scene a capture can change finished pixels: blur radius
    /// (four samples), bilinear reconstruction (one more) and block rounding, at quarter scale.</summary>
    public const int Reach = 32;
    /// <summary>Set by the last capture when finished pixels may differ anywhere in its regions, not only
    /// within <see cref="Reach"/> of what changed: a full capture, or a new adapted opacity.</summary>
    public bool WholeChanged { get; private set; }
    private bool _hasCapture;
    private Rect _lastContent;
    private int _lastWidth, _lastHeight;
    private uint _lastFallback;

    /// <param name="backdropUnchanged">The caller knows the pixels under these regions match the last capture.</param>
    public void Capture(int[] scene, int width, int height, Rect content, ProcessMaterialRegion[] regions, int count, uint fallback, bool backdropUnchanged = false)
    {
        int revision = CpuWorkers.BlurRevision;
        bool changed = _count != count || _blurRevision != revision; int offset = 0;
        for (int i = 0; i < count; i++) { changed |= _previous[i] != regions[i]; }
        WholeChanged = false;
        if (backdropUnchanged && !changed) { _stable = true; return; }
        // Incremental: the same regions at the same place over the same scene, and the caller knows where
        // the scene changed. Samples outside the dirty area keep their values; any change still blurs again.
        Rect dirty = Dirty;
        bool partial = !changed && _hasCapture && dirty.W > 0 && dirty.H > 0 && SameRect(content, _lastContent)
            && width == _lastWidth && height == _lastHeight && fallback == _lastFallback;
        long probe = FrameStats.Now;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; _previous[i] = r;
            int sampledWidth = SampleLength(r.Width, _sampleScale), sampledHeight = SampleLength(r.Height, _sampleScale);
            if (r.Effect == 0) { offset += sampledWidth * sampledHeight; continue; }
            int left = content.X + r.X, top = content.Y + r.Y, x0 = 0, y0 = 0, x1 = sampledWidth, y1 = sampledHeight;
            if (partial)
            {
                Rect hit = dirty.Intersect(new Rect(left, top, r.Width, r.Height));
                if (hit.W <= 0 || hit.H <= 0) { offset += sampledWidth * sampledHeight; continue; }
                x0 = (hit.X - left) / _sampleScale; y0 = (hit.Y - top) / _sampleScale;
                x1 = Math.Min(sampledWidth, (hit.Right - left + _sampleScale - 1) / _sampleScale);
                y1 = Math.Min(sampledHeight, (hit.Bottom - top + _sampleScale - 1) / _sampleScale);
            }
            changed |= Sample(scene, width, height, left, top, r, fallback, offset, sampledWidth, x0, y0, x1, y1);
            offset += sampledWidth * sampledHeight;
        }
        _hasCapture = true; _lastContent = content; _lastWidth = width; _lastHeight = height; _lastFallback = fallback;
        _count = count;
        _blurRevision = revision;
        _stable = !changed;
        FrameStats.SampleTicks += FrameStats.Now - probe; probe = FrameStats.Now;
        if (!changed) { return; }
        // A fresh capture can change any pixel; an incremental one only near the changed samples,
        // unless the adapted opacity (from the whole region's luminance range) moves below.
        if (!partial) { WholeChanged = true; }
        ReleasePainted();
        offset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; int sampledWidth = SampleLength(r.Width, _sampleScale), sampledHeight = SampleLength(r.Height, _sampleScale);
            int area = sampledWidth * sampledHeight;
            if (r.Effect == 2)
            {
                if (!CpuWorkers.TryBlur(_source, _filtered, offset, sampledWidth, sampledHeight))
                {
                    BoxBlur.Pass(_source, _scratch, offset, sampledWidth, sampledHeight, true);
                    BoxBlur.Pass(_scratch, _filtered, offset, sampledWidth, sampledHeight, false);
                }
                BlurRebuilds++;
            }
            else if (r.Effect != 0) { Array.Copy(_source, offset, _filtered, offset, area); }
            FrameStats.BlurTicks += FrameStats.Now - probe; probe = FrameStats.Now;
            double low = 1, high = 0;
            if (r.Effect != 0)
            {
                for (int k = offset; k < offset + area; k++)
                { double l = MaterialPolicy.Luminance(_filtered[k]); if (l < low) { low = l; } if (l > high) { high = l; } }
            }
            double newLow = r.Effect != 0 ? low : 0, newHigh = r.Effect != 0 ? high : 1;
            if (Math.Floor(newLow * 64) != Math.Floor(_low[i] * 64) || Math.Ceiling(newHigh * 64) != Math.Ceiling(_high[i] * 64)) { WholeChanged = true; }
            _low[i] = newLow; _high[i] = newHigh;
            FrameStats.RangeTicks += FrameStats.Now - probe; probe = FrameStats.Now;
            offset += area;
        }
    }

    private static bool SameRect(Rect a, Rect b) => a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H;

    /// <summary>Averages samples [x0,x1) x [y0,y1) of one region; true when any differs from before. A
    /// sample whose whole block lies inside the region and on screen (nearly all) takes the fast path: no
    /// bounds checks, red with blue summed in one word (16 bits a lane holds 16 x 255). Edge samples clip
    /// to the region and use the fallback off screen. Both give the same averages.</summary>
    private unsafe bool Sample(int[] scene, int width, int height, int left, int top, ProcessMaterialRegion r, uint fallback,
        int offset, int sampledWidth, int x0, int y0, int x1, int y1)
    {
        int scale = _sampleScale, area = scale * scale;
        // Full on-screen blocks: columns [fx0, fx1), rows [fy0, fy1).
        int fx0 = Math.Max(0, (-left + scale - 1) / scale), fy0 = Math.Max(0, (-top + scale - 1) / scale);
        int fx1 = width - left < 0 ? 0 : Math.Min(r.Width / scale, (width - left) / scale);
        int fy1 = height - top < 0 ? 0 : Math.Min(r.Height / scale, (height - top) / scale);
        int fastX0 = Math.Max(x0, fx0), fastX1 = Math.Min(x1, fx1);
        bool changed = false;
        fixed (int* pixels = scene)
        fixed (int* target = _source)
        {
            for (int y = y0; y < y1; y++)
            {
                bool inner = y >= fy0 && y < fy1 && fastX1 > fastX0;
                for (int x = x0; x < x1; x++)
                {
                    int color;
                    if (inner && x == fastX0)
                    {
                        int* slot = target + offset + y * sampledWidth + x;
                        int* block = pixels + (top + y * scale) * width + left + x * scale;
                        for (; x < fastX1; x++, slot++, block += scale)
                        {
                            uint redBlue = 0, green = 0;
                            int* row = block;
                            for (int dy = 0; dy < scale; dy++, row += width)
                            {
                                for (int dx = 0; dx < scale; dx++)
                                { uint c = (uint)row[dx]; redBlue += c & 0x00FF00FFu; green += (c >> 8) & 255; }
                            }
                            color = unchecked((int)0xff000000) | ((int)(redBlue >> 16) / area << 16)
                                | ((int)green / area << 8) | (int)(redBlue & 0xFFFF) / area;
                            changed |= *slot != color; *slot = color;
                        }
                        x--; continue;
                    }
                    int red = 0, greenSum = 0, blue = 0, samples = 0;
                    for (int dy = 0; dy < scale && y * scale + dy < r.Height; dy++)
                    for (int dx = 0; dx < scale && x * scale + dx < r.Width; dx++)
                    {
                        int sx = left + x * scale + dx, sy = top + y * scale + dy;
                        int c = sx < 0 || sy < 0 || sx >= width || sy >= height ? (int)fallback : pixels[sy * width + sx];
                        red += (c >> 16) & 255; greenSum += (c >> 8) & 255; blue += c & 255; samples++;
                    }
                    color = unchecked((int)0xff000000) | (red / samples << 16) | (greenSum / samples << 8) | blue / samples;
                    int at = offset + y * sampledWidth + x; changed |= target[at] != color; target[at] = color;
                }
            }
        }
        return changed;
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
        Rect clip = content.Intersect(new Rect(0, 0, width, height)).Intersect(Limit);
        if (clear)for (int y = clip.Y; y < clip.Bottom; y++)
        { Array.Fill(target, (int)(fallback | 0xff000000U), y * width + clip.X, clip.W); }
        int[]? painted = _stable ? Painted(regions, count, fallback, panel, navigation, popover) : null;
        int offset = 0, paintedOffset = 0;
        for (int i = 0; i < count; i++)
        {
            var r = regions[i]; uint tint = r.Role == 1 ? fallback : r.Role == 3 ? navigation : r.Role == 5 ? popover : panel;
            Rect rect = new(content.X + r.X, content.Y + r.Y, r.Width, r.Height);
            Rect visible = rect.Intersect(clip);
            int opacity = OpacityFor(i, r, tint);
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
        if (pixels == 0 || pixels > MaterialResources.PaintedBudgetBytes / sizeof(int)) { return null; }
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
                int opacity = OpacityFor(i, r, tint);
                for (int py = 0; py < r.Height; py++) { MaterialRow(offset, r, py, tint, opacity, _painted, at); at += r.Width; }
            }
            offset += SampleLength(r.Width, _sampleScale) * SampleLength(r.Height, _sampleScale);
        }
        _paintedFallback = fallback; _paintedPanel = panel; _paintedNavigation = navigation; _paintedPopover = popover; _paintedValid = true;
        return _painted;
    }

    public unsafe void Compose(int[] frame, int[] target, int width, int height, Rect content,
        ProcessMaterialRegion[] regions, int count, uint fallback, uint panel, uint navigation, uint popover)
    {
        Paint(target, width, height, content, regions, count, fallback, panel, navigation, popover, true);
        Rect clip = content.Intersect(new Rect(0, 0, width, height)).Intersect(Limit);
        if (!clip.IsEmpty && CpuWorkers.TryCompose(frame, (clip.Y - content.Y) * content.W + clip.X - content.X,
            content.W, target, clip.Y * width + clip.X, width, clip.W, clip.H)) { return; }
        if (clip.IsEmpty) { return; }
        fixed (int* source = frame)
        fixed (int* output = target)
        {
            for (int y = clip.Y; y < clip.Bottom; y++)
            { Over(source + (y - content.Y) * content.W + clip.X - content.X, output + y * width + clip.X, clip.W); }
        }
    }

    /// <summary>Premultiplied foreground over the target: f + b * (255 - a) / 255 per channel, rounded.
    /// Red and blue share one multiply in 16-bit lanes; x / 255 rounded is (t + (t >> 8)) >> 8 with
    /// t = x + 128, exact for every 8-bit product. Channels that would overflow (a client's invalid
    /// premultiplication) take the per-channel formula, so every result equals it.</summary>
    internal static unsafe void Over(int* foreground, int* target, int count)
    {
        for (int i = 0; i < count; i++)
        {
            uint fg = (uint)foreground[i], alpha = fg >> 24;
            if (alpha == 255) { target[i] = (int)fg; continue; }
            if (alpha == 0) { continue; }
            uint bg = (uint)target[i], inverse = 255 - alpha;
            uint rb = (bg & 0x00FF00FFu) * inverse + 0x00800080u;
            rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
            uint g = (bg & 0x0000FF00u) * inverse + 0x00008000u;
            g = ((g + ((g >> 8) & 0x0000FF00u)) >> 8) & 0x0000FF00u;
            uint redBlue = (fg & 0x00FF00FFu) + rb, green = (fg & 0x0000FF00u) + g;
            if (((redBlue & 0x01000100u) | (green & 0x00010000u)) == 0) { target[i] = (int)(0xff000000u | redBlue | green); continue; }
            uint color = 0xff000000;
            for (int shift = 0; shift < 24; shift += 8)
            { color |= (((fg >> shift) & 255) + (((bg >> shift) & 255) * inverse + 127) / 255) << shift; }
            target[i] = (int)color;
        }
    }
}
