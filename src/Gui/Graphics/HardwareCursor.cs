using System;
using System.Runtime.InteropServices;
using Zenith.Gui.Shell;

namespace Zenith.Gui.Graphics;

/// <summary>
/// The pointer drawn by the display device (VMware SVGA II) over the frame. Moving it is a few
/// register writes: it never waits for a compose, a present or a scheduler tick, and with the
/// absolute pointer VMware shows it as the host's own pointer. Its image is rendered at the
/// device's resolution, sharp under any scale. Other devices keep the pointer drawn into frames
/// (<see cref="Cursor"/>). BSP and session thread only.
/// </summary>
internal static class HardwareCursor
{
#if !ARCH_ARM64
    [DllImport("*", EntryPoint = "zenith_svga_cursor_define"), SuppressGCTransition]
    private static extern unsafe ulong Define(ulong offset, uint* argb, ulong w, ulong h, ulong hotX, ulong hotY, uint* scratch, ulong scratchWords);
    [DllImport("*", EntryPoint = "zenith_svga_cursor_move"), SuppressGCTransition]
    private static extern ulong Move(ulong offset, ulong x, ulong y, ulong visible);
    [DllImport("*", EntryPoint = "zenith_cpu_vmware"), SuppressGCTransition]
    private static extern ulong OnVmware();
#endif

    /// <summary>On by default on VMware only: QEMU's vmware-svga can draw a pointer too, but test
    /// harnesses locate the pointer in screen captures, which never contain a device pointer.
    /// <c>fps cursor hardware|software</c> switches it at run time.</summary>
    public static bool Enabled { get; set; }
#if !ARCH_ARM64
        = OnVmware() == 1;
#endif
    private static ulong s_kind, s_moves;
    private static bool s_tried, s_shown;
    private static int s_x = -1, s_y = -1, s_percent, s_variant;
    private static CursorShape s_shape;

    /// <summary>The device draws the pointer: frames leave it out.</summary>
    public static bool Active => Ensure();

    public static string Status => "cursor hardware=" + Enabled + " kind=" + (s_kind switch { 1 => "alpha", 2 => "mask", _ => "none" })
        + " shown=" + s_shown + " scale=" + s_percent + "% moves=" + s_moves + " at=" + Input.PhysicalX + "," + Input.PhysicalY;

    /// <summary>Shows the device pointer at the polled physical position; false when frames must draw it.</summary>
    public static bool Show()
    {
        if (!Ensure()) { return false; }
        if (s_shape != Cursor.Shape || s_variant != Cursor.Variant)
        {
            // A new shape or set: define the device's image again at the same scale.
            s_kind = DefineImage(s_percent);
            if (s_kind == 0) { return false; }
            s_shown = false;
        }
        int x = Input.PhysicalX, y = Input.PhysicalY;
        if (s_shown && x == s_x && y == s_y) { return true; }
        if (!Place(x, y, true)) { s_kind = 0; return false; }
        s_x = x; s_y = y; s_shown = true; s_moves++;
        return true;
    }

    /// <summary>Before a screen that has no pointer (boot, power, panic, console).</summary>
    public static void Hide()
    {
        if (!s_shown) { return; }
        Place(Math.Max(0, s_x), Math.Max(0, s_y), false);
        s_shown = false;
    }

    /// <summary>After a mode or scale change: the image is defined again at the next use.</summary>
    public static void Reset()
    {
        Hide();
        s_tried = false; s_kind = 0;
    }

    private static bool Ensure()
    {
        if (!Enabled)
        {
            Hide();
            return false;
        }
        if (s_tried) { return s_kind != 0; }
        s_tried = true;
        s_kind = DefineImage(SessionDisplay.Percent);
        if (s_kind != 0) { s_percent = SessionDisplay.Percent; s_shown = false; }
        Zenith.Core.Log.Write("display", "pointer " + (s_kind == 0 ? "drawn into frames" : "drawn by the device") + " at " + SessionDisplay.Percent + "%");
        return s_kind != 0;
    }

    private static unsafe ulong DefineImage(int percent)
    {
#if ARCH_ARM64
        return 0;
#else
        var screen = SessionDisplay.Physical;
        if (!screen.Name.StartsWith("VMWare", StringComparison.OrdinalIgnoreCase)) { return 0; }
        var hhdm = Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response;
        if (hhdm == null) { return 0; }
        // The Meridian set at the size nearest above 24 logical pixels at this scale.
        s_shape = Cursor.Shape; s_variant = Cursor.Variant;
        int w = CursorImages.SizeFor(Cursor.Size * Math.Clamp(percent, 100, 300) / 100), h = w;
        uint[] image = CursorImages.Get(s_variant, s_shape, w, out int hotX, out int hotY);
        uint[] scratch = new uint[(w + 31) / 32 * h + w * h];
        fixed (uint* pixels = image)
        fixed (uint* work = scratch)
        {
            return Define(hhdm->Offset, pixels, (ulong)w, (ulong)h, (ulong)hotX, (ulong)hotY, work, (ulong)scratch.Length);
        }
#endif
    }

    private static unsafe bool Place(int x, int y, bool visible)
    {
#if ARCH_ARM64
        return false;
#else
        var hhdm = Cosmos.Kernel.Boot.Limine.Limine.HHDM.Response;
        return hhdm != null && Move(hhdm->Offset, (ulong)Math.Max(0, x), (ulong)Math.Max(0, y), visible ? 1UL : 0UL) != 0;
#endif
    }
}
