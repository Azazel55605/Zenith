using System.Runtime.InteropServices;

namespace Zenith.Core.Input;

/// <summary>
/// VMware's absolute pointer (vmmouse). VMware otherwise feeds the PS/2 mouse relative steps and
/// grabs the host pointer; with it the host reports where its own pointer is, so the guest pointer
/// follows it exactly and the window need not capture the mouse. The PS/2 packets that still
/// arrive only announce new data; buttons and the wheel come from here while it is on. Started on
/// VMware only: QEMU test input stays relative (<c>pointer=absolute</c> forces it on any
/// hypervisor, <c>pointer=relative</c> turns it off). BSP only.
/// </summary>
internal static class AbsolutePointer
{
#if !ARCH_ARM64
    [DllImport("*", EntryPoint = "zenith_vmmouse_enable"), SuppressGCTransition]
    private static extern ulong Enable(ulong enable, ulong anyHypervisor);
    [DllImport("*", EntryPoint = "zenith_vmmouse_poll"), SuppressGCTransition]
    private static extern unsafe long Read(int* state);
    [DllImport("*", EntryPoint = "zenith_vmmouse_counter"), SuppressGCTransition]
    private static extern ulong Counter(ulong which);
#endif
    private static readonly int[] s_state = new int[7];
    private static int s_wheel;

    public static bool Active { get; private set; }
    public static bool Left { get; private set; }
    public static bool Right { get; private set; }
    public static bool Middle { get; private set; }

    public static string Status
#if !ARCH_ARM64
        => "pointer absolute=" + Active + " packets=" + Counter(1) + " errors=" + Counter(2);
#else
        => "pointer absolute=False";
#endif

    public static void Start(string commandLine)
    {
#if !ARCH_ARM64
        if (Active || commandLine.Contains("pointer=relative")) { return; }
        Active = Enable(1, commandLine.Contains("pointer=absolute") ? 1UL : 0UL) == 1;
        if (Active) { Log.Write("input", "absolute pointer (vmmouse) on"); }
#endif
    }

    public static void Stop()
    {
#if !ARCH_ARM64
        if (Active) { Enable(0, 1); }
#endif
        Active = false;
    }

    /// <summary>Applies queued packets: true with the new physical position when any carried one.</summary>
    public static unsafe bool Poll(int width, int height, ref int x, ref int y)
    {
#if ARCH_ARM64
        return false;
#else
        if (!Active) { return false; }
        int[] state = s_state;
        System.Array.Clear(state);
        long read;
        fixed (int* p = state) { read = Read(p); }
        if (read < 0)
        {
            Active = false;
            Log.Write("input", "absolute pointer stopped after a queue error; relative PS/2 input continues");
            return false;
        }
        if (read == 0) { return false; }
        Left = (state[2] & 1) != 0; Right = (state[2] & 2) != 0; Middle = (state[2] & 4) != 0;
        s_wheel += state[3];
        bool moved = false;
        if ((state[4] & 1) != 0)
        {
            x = (int)((long)state[0] * width / 65536); y = (int)((long)state[1] * height / 65536);
            moved = true;
        }
        if ((state[4] & 2) != 0) { x += state[5]; y += state[6]; moved = true; }
        x = System.Math.Clamp(x, 0, width - 1); y = System.Math.Clamp(y, 0, height - 1);
        return moved;
#endif
    }

    /// <summary>Wheel steps since the last call, in the PS/2 sign (positive is toward the user).</summary>
    public static int TakeWheel()
    {
        int wheel = s_wheel; s_wheel = 0; return wheel;
    }
}
