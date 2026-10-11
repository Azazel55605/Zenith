using System.Runtime.InteropServices;

namespace Zenith.Core.Security;

/// <summary>CPU/hypervisor RDSEED is the trusted source on the initial x64 target.</summary>
internal static class HardwareEntropy
{
    public static readonly IEntropySource Source = new SeedEntropySource(TryWord);
    [DllImport("*", EntryPoint = "zenith_rdseed_word")]
    [SuppressGCTransition]
    private static extern int ReadSeed(out ulong word);
    private static bool TryWord(out ulong word) => ReadSeed(out word) != 0;
}
