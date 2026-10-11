using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Count frames delivered by the driver separately from checked stack ingress.</summary>
public static class EthernetIngress
{
    private static int s_received, s_accepted;
    public static int Received => Volatile.Read(ref s_received);
    public static int Accepted => Volatile.Read(ref s_accepted);

    public static bool Accept(byte[]? frame, int length)
    {
        Interlocked.Increment(ref s_received);
        bool accepted = EthernetGuard.Accept(frame, length);
        if (accepted) { Interlocked.Increment(ref s_accepted); }
        return accepted;
    }
}
