using System;
using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>E1000E receive activation, polling registration and delivery diagnostics.</summary>
public static class E1000ReceiveFilter
{
    public static bool Observed { get; private set; }
    public static bool InitiallyValid { get; private set; }
    private static int s_interrupts, s_receiveInterrupts;
    private static Action? s_poll;
    public static int Polls { get; private set; }
    public static int PolledFrames { get; private set; }
    public static int Interrupts => Volatile.Read(ref s_interrupts);
    public static int ReceiveInterrupts => Volatile.Read(ref s_receiveInterrupts);

    public static void RegisterPoll(Action poll) => s_poll = poll;

    // Caller masks interrupts: poll and IRQ must not consume the same descriptor.
    public static void Poll()
    {
        if (s_poll is null) { return; }
        Polls++;
        int before = EthernetIngress.Received;
        s_poll();
        PolledFrames += EthernetIngress.Received - before;
    }

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
