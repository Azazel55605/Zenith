using System;
using System.Runtime.CompilerServices;

namespace Zenith.Core.Input;

/// <summary>Drains pending mouse packets on the BSP even if a device interrupt was lost.</summary>
internal static class MousePolling
{
    // The pinned build adapter binds this to the IRQ-scoped MouseManager.Poll
    // in copied Cosmos references. Fail closed if the adapter was omitted.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Drain() => throw new PlatformNotSupportedException("Zenith mouse polling adapter missing");
}
