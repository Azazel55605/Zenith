using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Zenith.Core;

/// <summary>
/// Kernel log. Lines go to the serial port (COM1, which QEMU forwards with <c>-serial stdio</c>),
/// into a ring buffer that the <c>dmesg</c> command prints, and, once the root filesystem is
/// up, to <c>/var/log/boot.log</c> (the previous boot's log is kept as <c>boot.log.1</c>).
/// Cosmos keeps its serial writer internal, so this binds to it with [UnsafeAccessor]
/// (see docs/cosmos/articles/dev/accessing-internals.md): it may break on a Cosmos upgrade.
/// </summary>
internal static class Log
{
    private const int Capacity = 256;
    private static readonly Queue<string> s_lines = new();
    private static readonly object s_lock = new();
    private static string? s_file;

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "WriteString")]
    private static extern void SerialWriteString(
        [UnsafeAccessorType("Cosmos.Kernel.Core.IO.Serial, Cosmos.Kernel.Core")] object? serial, string text);

    /// <summary>A snapshot of the ring buffer.</summary>
    public static string[] Lines
    {
        get
        {
            lock (s_lock)
            {
                return s_lines.ToArray();
            }
        }
    }

    public static void Write(string tag, string message)
    {
        long ms = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        string line = "[" + (ms / 1000).ToString().PadLeft(5) + "." + (ms % 1000).ToString().PadLeft(3, '0') + "] " + tag + ": " + message;

        // Shell commands log from worker threads, the desktop from the main loop.
        lock (s_lock)
        {
            if (s_lines.Count == Capacity)
            {
                s_lines.Dequeue();
            }

            s_lines.Enqueue(line);
            SerialWriteString(null, "[zenith] " + line + "\n");

            if (s_file is not null)
            {
                try
                {
                    File.AppendAllText(s_file, line + "\n");
                }
                catch (Exception)
                {
                    s_file = null;   // the filesystem went away; keep logging to serial
                }
            }
        }
    }

    /// <summary>Stops mirroring to the file, before the filesystem holding it is unmounted.</summary>
    public static void StopPersisting()
    {
        lock (s_lock)
        {
            s_file = null;
        }
    }

    /// <summary>Starts mirroring the log to <paramref name="path"/>, rotating the previous file to <c>.1</c>.</summary>
    public static void PersistTo(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.WriteAllText(path, string.Join("\n", s_lines) + "\n");
            s_file = path;
        }
        catch (Exception e)
        {
            Write("log", "cannot write " + path + ": " + e.Message);
        }
    }
}
