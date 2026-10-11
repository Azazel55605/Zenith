using System;
using Cosmos.Kernel.System;

namespace Zenith.Core.Console;

internal static class SerialPanic
{
    public static void Show(Exception exception)
    {
        Log.Write("panic", exception.GetType().Name + ": " + exception.Message);
        foreach (string frame in (exception.StackTrace ?? "(no stack trace)").Split('\n'))
        { Log.Write("panic", frame.Trim()); }
        Log.WriteSerial("\r\n[console] Zenith stopped. Press R on serial to restart.\r\n");
        while (true)
        {
            int value = SerialPort.Read();
            if (value == 'r' || value == 'R') { Power.Reboot(); }
            Power.Halt();
        }
    }
}
