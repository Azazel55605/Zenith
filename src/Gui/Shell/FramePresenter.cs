using System;
using System.Runtime.InteropServices;
using Cosmos.Kernel.System.Graphics;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>
/// Puts a logical frame on the screen: only the damaged rectangle is copied. On VMware SVGA II the rows
/// go straight to the visible framebuffer, scaled while they are written, with one update command for the
/// rectangle (<c>svga.h</c>). The pinned driver's own <c>Display()</c> copies its whole VRAM back buffer to
/// the front and waits for a full-screen update on every call, whatever changed: about 25 ms a frame at
/// 1280x800. Other drivers, or a refused direct present, take the driver path: integer scale expands
/// each row once and copies it to the other scale rows; drivers read width*height pixels at stride width.
/// </summary>
internal sealed class FramePresenter
{
    /// <summary>For the screens outside the desktop (boot, login, fades, console): one presenter, BSP only.</summary>
    public static readonly FramePresenter Shared = new();
    /// <summary><c>fps direct off</c> compares against the driver path.</summary>
    public static bool Direct = true;
    public static long DirectPresents, DriverPresents;
    // The driver's VRAM back buffer misses every direct present, and its Display() copies all of it to the
    // screen: the first driver present after a direct one must therefore be the whole frame.
    private static bool s_backStale;
    private int[] _buffer = Array.Empty<int>();

#if !ARCH_ARM64
    [DllImport("*", EntryPoint = "zenith_svga_present"), SuppressGCTransition]
    private static extern unsafe ulong SvgaPresent(ulong offset, int* source, ulong stride, ulong x, ulong y, ulong w, ulong h, ulong scale);
    [DllImport("*", EntryPoint = "zenith_svga_counter"), SuppressGCTransition]
    private static extern ulong SvgaCounter(ulong which);
#endif
    public static string Status
#if !ARCH_ARM64
        => "present direct=" + Direct + " direct-frames=" + DirectPresents + " driver-frames=" + DriverPresents
            + " refused=" + SvgaCounter(1) + " fifo-waits=" + SvgaCounter(2);
#else
        => "present direct=False driver-frames=" + DriverPresents;
#endif

    /// <summary>A whole frame of <paramref name="screen"/>'s logical size.</summary>
    public static void PresentFrame(Canvas screen, int[] frame, int width, int height)
        => Shared.Present(screen, frame, width, height, new Rect(0, 0, width, height));

    private static unsafe bool TryDirect(Canvas target, int[] frame, int width, Rect damage, int scale)
    {
#if ARCH_ARM64
        return false;
#else
        if (!Direct || !target.Name.StartsWith("VMWare", StringComparison.OrdinalIgnoreCase)) { return false; }
        var hhdm = Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response;
        if (hhdm == null) { return false; }
        fixed (int* pixels = &frame[damage.Y * width + damage.X])
        {
            return SvgaPresent(hhdm->Offset, pixels, (ulong)width, (ulong)damage.X, (ulong)damage.Y,
                (ulong)damage.W, (ulong)damage.H, (ulong)scale) == 1;
        }
#endif
    }

    public void Present(Canvas screen, int[] frame, int width, int height, Rect damage)
    {
        damage = damage.Intersect(new Rect(0, 0, width, height));
        if (damage.W <= 0 || damage.H <= 0 || frame.Length < width * height) { return; }
        Canvas target = screen is DisplayCanvas scaled ? scaled.Physical : screen;
        int scale = screen is DisplayCanvas s ? s.Scale : 1;
        if (TryDirect(target, frame, width, damage, scale)) { DirectPresents++; s_backStale = true; return; }
        DriverPresents++;
        if (s_backStale) { damage = new Rect(0, 0, width, height); s_backStale = false; }
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
