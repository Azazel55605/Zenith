using System.Threading;

namespace Zenith.Core.Processes;

/// <summary>A thread was woken by the kernel loop and should run now; see <c>KernelHandoff</c>.</summary>
internal static class WakeSignal
{
    private static int s_requested;
    public static void Request() => Volatile.Write(ref s_requested, 1);
    public static bool Take() => Interlocked.Exchange(ref s_requested, 0) != 0;
}
