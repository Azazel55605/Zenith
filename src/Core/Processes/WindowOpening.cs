using System;

namespace Zenith.Core.Processes;

/// <summary>The size a resizable native window opens at. A program asks for its compact size (at most 640x480, the
/// surface limit of call 610); with the "fit" window setting the desktop scales that up, keeping its proportions,
/// to about three quarters of the free screen width or four fifths of its height, whichever is smaller, and at most twice the request.</summary>
internal static class WindowOpening
{
    public const int MinimumWidth = 600, MinimumHeight = 480;
    /// <summary>The opening size, or null to keep the requested one (no room to grow by a tenth, or the result is
    /// outside the resize range).</summary>
    public static (int Width, int Height)? Fit(int width, int height, int workWidth, int workHeight, int maximumWidth, int maximumHeight)
    {
        if (width <= 0 || height <= 0 || workWidth <= 0 || workHeight <= 0) { return null; }
        // Scale in 1/1000 steps: freestanding integer math, the same on every CPU.
        long scale = Math.Min(Math.Min(workWidth * 750L / width, workHeight * 800L / height), 2000);
        if (scale < 1100) { return null; }
        int w = (int)(width * scale / 1000) & ~1, h = (int)(height * scale / 1000) & ~1;
        w = Math.Min(w, Math.Min(maximumWidth, ProcessSurface.MaximumWidth)); h = Math.Min(h, Math.Min(maximumHeight, ProcessSurface.MaximumHeight));
        if (w < MinimumWidth || h < MinimumHeight || (long)w * h > ProcessSurface.MaximumPixels) { return null; }
        return (w, h);
    }
}
