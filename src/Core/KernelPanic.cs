namespace Zenith.Core;

/// <summary>
/// Deliberate kernel panics, for testing the panic path (the <c>crash</c> command, like Linux's
/// sysrq-c). The main loop checks <see cref="Requested"/> and throws from there, outside any
/// shell command's exception handler.
/// </summary>
internal static class KernelPanic
{
    public static string? Requested { get; private set; }

    public static void Request(string reason) => Requested = reason;
}
