using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MyOS.Core;

/// <summary>
/// Kernel log. Lines go to the serial port (COM1, which QEMU forwards with <c>-serial stdio</c>)
/// and into a ring buffer that the <c>dmesg</c> command prints.
/// Cosmos keeps its serial writer internal, so this binds to it with [UnsafeAccessor]
/// (see docs/cosmos/articles/dev/accessing-internals.md): it may break on a Cosmos upgrade.
/// </summary>
internal static class Log
{
    private const int Capacity = 256;
    private static readonly Queue<string> s_lines = new();

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "WriteString")]
    private static extern void SerialWriteString(
        [UnsafeAccessorType("Cosmos.Kernel.Core.IO.Serial, Cosmos.Kernel.Core")] object? serial, string text);

    public static IEnumerable<string> Lines => s_lines;

    public static void Write(string tag, string message)
    {
        long ms = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        string line = "[" + (ms / 1000).ToString().PadLeft(5) + "." + (ms % 1000).ToString().PadLeft(3, '0') + "] " + tag + ": " + message;

        if (s_lines.Count == Capacity)
        {
            s_lines.Dequeue();
        }

        s_lines.Enqueue(line);
        SerialWriteString(null, "[myos] " + line + "\n");
    }
}
