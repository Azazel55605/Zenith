using System.Runtime.InteropServices;

namespace Zenith.Core.Console;

/// <summary>x64 COM1 receive polling; Cosmos owns UART initialization/transmit.</summary>
internal static class SerialPort
{
    [DllImport("*", EntryPoint = "zenith_serial_read")]
    [SuppressGCTransition]
    public static extern int Read(); // -1 empty, -2 receive error, otherwise ASCII byte
}
