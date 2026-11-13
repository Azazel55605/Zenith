using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Frames handed to a network device and their bytes (Observatory's sent rate). The build
/// patch calls <see cref="Count"/> before every device send in the Cosmos stack.</summary>
public static class EthernetEgress
{
    private static int s_frames;
    private static long s_bytes;
    public static int Frames => Volatile.Read(ref s_frames);
    public static long Bytes => Interlocked.Read(ref s_bytes);

    public static void Count(int length)
    {
        Interlocked.Increment(ref s_frames);
        if (length > 0) { Interlocked.Add(ref s_bytes, length); }
    }
}
