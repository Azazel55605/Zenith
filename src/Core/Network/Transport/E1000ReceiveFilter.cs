using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Activate the exact-match address even when firmware supplied its MAC bytes.</summary>
public static class E1000ReceiveFilter
{
    public static bool Observed { get; private set; }
    public static bool InitiallyValid { get; private set; }
    private static int s_interrupts, s_receiveInterrupts;
    public static int Interrupts => Volatile.Read(ref s_interrupts);
    public static int ReceiveInterrupts => Volatile.Read(ref s_receiveInterrupts);

    public static void ObserveInterrupt(uint cause)
    {
        Interlocked.Increment(ref s_interrupts);
        if ((cause & 0x90u) != 0) { Interlocked.Increment(ref s_receiveInterrupts); }
    }

    public static uint Activate(uint high)
    {
        InitiallyValid = (high & 0x80000000u) != 0;
        Observed = true;
        return high | 0x80000000u;
    }
}
