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
    [DllImport("*", EntryPoint = "zenith_svga_present_filtered"), SuppressGCTransition]
    private static extern unsafe ulong SvgaPresentFiltered(ulong offset, int* frame, ulong width, ulong height, ulong x, ulong y, ulong w, ulong h, ulong percent);
    [DllImport("*", EntryPoint = "zenith_scale_filtered"), SuppressGCTransition]
    private static extern unsafe ulong ScaleFiltered(int* dst, ulong stride, int* frame, ulong width, ulong height, ulong x, ulong y, ulong w, ulong h,
        ulong percent, ulong deviceWidth, ulong deviceHeight, ulong* rect);
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
        int percent = screen is DisplayCanvas p ? p.Percent : 100;
        if (percent is not (100 or 200)) { PresentFiltered(target, frame, width, height, damage, percent); return; }
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

    /// <summary>125/150/175%: the logical frame resampled into device pixels (bilinear, native).</summary>
    private unsafe void PresentFiltered(Canvas target, int[] frame, int width, int height, Rect damage, int percent)
    {
#if ARCH_ARM64
        throw new NotSupportedException("fractional display scales need the x64 presenter");
#else
        if (Direct && target.Name.StartsWith("VMWare", StringComparison.OrdinalIgnoreCase)
            && Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response is var hhdm && hhdm != null)
        {
            fixed (int* pixels = frame)
            {
                if (SvgaPresentFiltered(hhdm->Offset, pixels, (ulong)width, (ulong)height, (ulong)damage.X, (ulong)damage.Y,
                    (ulong)damage.W, (ulong)damage.H, (ulong)percent) == 1)
                { DirectPresents++; s_backStale = true; return; }
            }
        }
        DriverPresents++;
        if (s_backStale) { damage = new Rect(0, 0, width, height); s_backStale = false; }
        int dw = target.Width, dh = target.Height;
        // The widest device rectangle a logical damage can map to: one logical pixel more on each side.
        int cw = Math.Min(dw, (damage.W + 3) * percent / 100 + 2), ch = Math.Min(dh, (damage.H + 3) * percent / 100 + 2);
        if (_buffer.Length < cw * ch) { _buffer = new int[cw * ch]; }
        ulong* rect = stackalloc ulong[4];
        fixed (int* output = _buffer)
        fixed (int* pixels = frame)
        {
            if (ScaleFiltered(output, (ulong)cw, pixels, (ulong)width, (ulong)height, (ulong)damage.X, (ulong)damage.Y,
                (ulong)damage.W, (ulong)damage.H, (ulong)percent, (ulong)dw, (ulong)dh, rect) != 1) { return; }
        }
        int rw = (int)rect[2], rh = (int)rect[3];
        if (rw != cw) { for (int row = 1; row < rh; row++) { Array.Copy(_buffer, row * cw, _buffer, row * rw, rw); } }
        target.DrawArray(_buffer, (int)rect[0], (int)rect[1], rw, rh);
        target.Display();
#endif
    }
}
