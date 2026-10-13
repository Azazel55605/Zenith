using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Buffers.Binary;

namespace Zenith.Core.Processes;

internal sealed record ProcessArguments(byte[] Bytes, ulong StackPointer)
{
    public const ulong Top = 0x7fff0000;
    public const int StackBytes = 65536;
    public static ProcessArguments Create(IReadOnlyList<string> arguments)
    {
        if (arguments.Count is < 1 or > 32) { throw new IOException("native program expects 1..32 arguments including its name"); }
        byte[] bytes = new byte[StackBytes]; int cursor = bytes.Length; var pointers = new ulong[arguments.Count]; int total = 0;
        for (int i = arguments.Count - 1; i >= 0; i--)
        {
            byte[] value = Encoding.UTF8.GetBytes(arguments[i]); total += value.Length + 1;
            if (value.Length > 1024 || total > 8192 || arguments[i].Contains('\0')) { throw new IOException("native argument bounds"); }
            cursor -= value.Length + 1; value.CopyTo(bytes, cursor); pointers[i] = Top - StackBytes + (ulong)cursor;
        }
        cursor = (cursor - (arguments.Count + 5) * 8) & ~15;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(cursor), (ulong)arguments.Count);
        for (int i = 0; i < pointers.Length; i++) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(cursor + 8 + i * 8), pointers[i]); }
        // argv terminator, empty envp and terminating auxiliary-vector pair are zero.
        return new(bytes, Top - StackBytes + (ulong)cursor);
    }
}
