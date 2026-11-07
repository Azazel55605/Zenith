using System;

namespace Zenith.Gui.Graphics;

/// <summary>One bounded software renderer policy shared by clients, chrome and shell.
/// Preferences are never changed by a rendering fallback.</summary>
internal sealed class MaterialPolicy
{
    public uint Translucency { get; private set; } = 40;
    public uint Requested { get; private set; }
    public uint Effective => (Requested & 1) == 0 ? 0 : Requested & 7;
    public uint Reason { get; private set; }
    private int _blurArea;
    public const int FrameBlurPixels = 393216;
    public void Begin(uint requested, uint translucency = 40)
    { Translucency = Math.Min(translucency, 100); Requested = requested & 7; Reason = (Requested & 1) == 0 && Requested != 0 ? 1U : 0; _blurArea = 0; }
    public void ResourceFallback() { Reason = 3; }
    /// <summary>The blur budget and reason so far, so a cached lower scene charges what drawing it would.</summary>
    public (int BlurArea, uint Reason) Spent => (_blurArea, Reason);
    public void Restore((int BlurArea, uint Reason) spent) { _blurArea = spent.BlurArea; Reason = spent.Reason; }
    public int Effect(int requested, uint tint, uint text, uint secondary, int area)
    {
        if ((Effective & 1) == 0 || requested == 0 || Translucency == 0) { return 0; }
        int effect = requested == 2 && (Effective & 2) != 0 ? 2 : 1;
        if (effect == 2 && (area < 0 || area > FrameBlurPixels - _blurArea)) { effect = 1; Reason = 4; }
        int opacity = Opacity(tint, text, secondary, effect, Translucency);
        if (opacity == 255) { Reason = 5; return 0; }
        if (effect == 2) { _blurArea += area; }
        return effect;
    }
    private sealed record OpacityMemo(uint Tint, uint Text, uint Secondary, int Effect, uint Translucency, int Value);
    private static readonly OpacityMemo?[] Opacities = new OpacityMemo[32];
    private static int s_slot;
    public static int Opacity(uint tint, uint text, uint secondary, int effect, uint translucency = 40)
    {
        if (effect == 0) { return 255; }
        translucency = Math.Min(translucency, 100);
        foreach (var item in Opacities)
        { if (item is not null && item.Tint == tint && item.Text == text && item.Secondary == secondary && item.Effect == effect && item.Translucency == translucency) { return item.Value; } }
        int result = CalculateOpacity(tint, text, secondary, effect, translucency);
        int slot = System.Threading.Interlocked.Increment(ref s_slot) & 31;
        System.Threading.Volatile.Write(ref Opacities[slot], new OpacityMemo(tint, text, secondary, effect, translucency, result));return result;
    }
    private static int CalculateOpacity(uint tint, uint text, uint secondary, int effect, uint translucency = 40)
    {
        // Map the complete user range onto the readable range, rather than
        // letting most high-strength choices collapse onto the same contrast floor.
        for (int opacity = effect == 2 ? 145 : 170; opacity <= 255; opacity += 2)
        { if (Readable(tint, text, opacity) && Readable(tint, secondary, opacity)) { return 255 - (255 - opacity) * (int)translucency / 100; } }
        return 255;
    }
    // Linear light for each sRGB channel value, so measuring a backdrop costs a table lookup per pixel.
    private static readonly double[] s_linear = BuildLinear();
    private static double[] BuildLinear()
    {
        var table = new double[256];
        for (int i = 0; i < 256; i++) { double x = i / 255.0; table[i] = x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        return table;
    }
    /// <summary>Relative luminance of an RGB value, 0 for black to 1 for white.</summary>
    public static double Luminance(int color) => .2126 * s_linear[(color >> 16) & 255] + .7152 * s_linear[(color >> 8) & 255] + .0722 * s_linear[color & 255];
    private static int Gray(double luminance)
    {
        int v = 0;
        while (v < 255 && s_linear[v + 1] <= luminance) { v++; }
        return unchecked((int)0xff000000) | v << 16 | v << 8 | v;
    }

    private sealed record AdaptedMemo(uint Tint, uint Text, uint Secondary, int Effect, uint Translucency, int Low, int High, int Value);
    private static readonly AdaptedMemo?[] Adapted = new AdaptedMemo[64];
    private static int s_adaptedSlot;
    /// <summary>The opacity for a backdrop whose blurred luminance runs from <paramref name="low"/> to <paramref name="high"/>:
    /// the least that keeps both text colors at 4.5:1 against everything actually behind, scaled by translucency like
    /// <see cref="Opacity"/>. It never exceeds <paramref name="worst"/>, the clamp that assumes any backdrop at all.</summary>
    public static int AdaptedOpacity(int worst, uint tint, uint text, uint secondary, int effect, uint translucency, double low, double high)
    {
        if (effect == 0 || worst >= 255) { return worst; }
        translucency = Math.Min(translucency, 100);
        // Rounded outward to 1/64, so the answer is never laxer than the measurement and memoizes well.
        int lowStep = Math.Clamp((int)Math.Floor(low * 64), 0, 64), highStep = Math.Clamp((int)Math.Ceiling(high * 64), lowStep, 64);
        foreach (var item in Adapted)
        {
            if (item is not null && item.Tint == tint && item.Text == text && item.Secondary == secondary && item.Effect == effect
                && item.Translucency == translucency && item.Low == lowStep && item.High == highStep) { return Math.Min(item.Value, worst); }
        }
        int dark = Gray(lowStep / 64.0), light = Gray(highStep / 64.0), floor = 255;
        for (int opacity = effect == 2 ? 145 : 170; opacity <= 255; opacity += 2)
        {
            if (ReadableOver(tint, text, opacity, dark, light) && ReadableOver(tint, secondary, opacity, dark, light)) { floor = opacity; break; }
        }
        int result = 255 - (255 - floor) * (int)translucency / 100;
        int slot = System.Threading.Interlocked.Increment(ref s_adaptedSlot) & 63;
        System.Threading.Volatile.Write(ref Adapted[slot], new AdaptedMemo(tint, text, secondary, effect, translucency, lowStep, highStep, result));
        return Math.Min(result, worst);
    }
    private static bool ReadableOver(uint tint, uint foreground, int opacity, int darkBackdrop, int lightBackdrop)
    {
        uint dark = (uint)RoundedComposition.Blend((int)tint, darkBackdrop, opacity);
        uint light = (uint)RoundedComposition.Blend((int)tint, lightBackdrop, opacity);
        double low = Math.Min(Light(dark), Light(light)), high = Math.Max(Light(dark), Light(light)), ink = Light(foreground);
        if (ink >= low && ink <= high) { return false; }
        return (Math.Max(ink, ink < low ? low : high) + .05) / (Math.Min(ink, ink < low ? low : high) + .05) >= 4.5;
    }
    private static bool Readable(uint tint, uint foreground, int opacity)
    {
        uint dark = (uint)RoundedComposition.Blend((int)tint, unchecked((int)0xff000000), opacity);
        uint light = (uint)RoundedComposition.Blend((int)tint, -1, opacity);
        double low = Math.Min(Light(dark), Light(light)), high = Math.Max(Light(dark), Light(light)), ink = Light(foreground);
        // If the possible backgrounds straddle the ink, some backdrop has 1:1 contrast.
        if (ink >= low && ink <= high) { return false; }
        return (Math.Max(ink, ink < low ? low : high) + .05) / (Math.Min(ink, ink < low ? low : high) + .05) >= 4.5;
    }
    private static double Light(uint c)
    {
        double Channel(int shift) { double x = ((c >> shift) & 255) / 255.0;return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
        return .2126 * Channel(16) + .7152 * Channel(8) + .0722 * Channel(0);
    }
}

internal static class MaterialResources
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static int BudgetBytes { get; private set; } = MaximumBytes;
    public static int UsedBytes { get; private set; }
    public static bool ReserveBytes(int bytes)
    {
        if (bytes > BudgetBytes - UsedBytes || bytes < -UsedBytes) { return false; }
        UsedBytes += bytes; return true;
    }
    public static void ReleaseBytes(int bytes) { UsedBytes -= bytes; }
    // Finished pixels only save time, so running out of this budget costs speed and nothing else.
    public const int MaximumPaintedBytes = 16 * 1024 * 1024;
    public static int PaintedBudgetBytes { get; private set; } = MaximumPaintedBytes;
    public static void Configure(ulong physicalBytes)
    {
        BudgetBytes = (int)Math.Clamp(physicalBytes / 32, (ulong)MaximumBytes, 128UL * 1024 * 1024);
        PaintedBudgetBytes = (int)Math.Clamp(physicalBytes / 64, (ulong)MaximumPaintedBytes, 64UL * 1024 * 1024);
    }
    public static int PaintedBytes { get; private set; }
    public static bool ReservePainted(int bytes)
    {
        if (bytes < 0 || bytes > PaintedBudgetBytes - PaintedBytes) { return false; }
        PaintedBytes += bytes; return true;
    }
    public static void ReleasePainted(int bytes) { PaintedBytes -= bytes; }
    public static readonly MaterialPolicy Policy = new();
}

internal sealed class MaterialCache : IDisposable
{
    private readonly int _capacity;
    private bool _disposed, _blur;
    private int _sampleScale = 1;
    private int _allocated, _bytes;
    public MaterialComposition Composition { get; private set; } = new(0);
    public MaterialCache(int capacity) { _capacity = capacity; }
    public bool Ensure() => Ensure(_capacity, true);
    public bool Ensure(int pixels, bool blur, int sampleScale = 1)
    {
        if (_disposed || pixels < 1 || pixels > _capacity || sampleScale is not (1 or 4)) { return false; }
        if (_allocated >= pixels && (!blur || _blur) && _sampleScale == sampleScale) { return true; }
        int bytes = pixels * (blur ? 12 : 4);
        if (!MaterialResources.ReserveBytes(bytes - _bytes)) { MaterialResources.Policy.ResourceFallback(); return false; }
        Composition.ReleasePainted(); Composition = new(pixels, blur, sampleScale); _sampleScale = sampleScale; _allocated = pixels; _blur = blur; _bytes = bytes; return true;
    }
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true;
        Composition.ReleasePainted(); MaterialResources.ReleaseBytes(_bytes); _bytes = 0; Composition = new(0);
    }
}
