using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Count frames delivered by the driver separately from checked stack ingress, and the
/// bytes of the frames delivered (Observatory's received rate).</summary>
public static class EthernetIngress
{
    private static int s_received, s_accepted;
    private static long s_bytes;
    public static int Received => Volatile.Read(ref s_received);
    public static int Accepted => Volatile.Read(ref s_accepted);
    public static long Bytes => Interlocked.Read(ref s_bytes);

    public static bool Accept(byte[]? frame, int length)
    {
        Interlocked.Increment(ref s_received);
        if (frame is not null && length > 0) { Interlocked.Add(ref s_bytes, length); }
        bool accepted = EthernetGuard.Accept(frame, length);
        if (accepted) { Interlocked.Increment(ref s_accepted); }
        return accepted;
    }
}
