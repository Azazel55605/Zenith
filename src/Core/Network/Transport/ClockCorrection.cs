using System.Threading;

namespace Zenith.Core.Network.Transport;

/// <summary>Shared UTC adjustment applied once at the platform DateTime clock seam.</summary>
public static class ClockCorrection
{
    private static long s_ticks;
    public static long Ticks => Interlocked.Read(ref s_ticks);
    public static long Adjust(long rawTicks) => rawTicks > 0 ? checked(rawTicks + Ticks) : rawTicks;
    public static void Synchronize(long targetTicks, long observedCorrectedTicks)
    {
        Interlocked.Exchange(ref s_ticks, checked(targetTicks - observedCorrectedTicks + Ticks));
    }
}
