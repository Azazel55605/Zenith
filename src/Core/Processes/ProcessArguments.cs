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
    public const int MaximumEnvironment = 64, MaximumEnvironmentBytes = 8192;

    public static ProcessArguments Create(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (arguments.Count is < 1 or > 32) { throw new IOException("native program expects 1..32 arguments including its name"); }
        var variables = new List<string>();
        int environmentBytes = 0;
        if (environment is not null)
        {
            if (environment.Count > MaximumEnvironment) { throw new IOException("native environment count exceeds 64"); }
            foreach (var pair in environment)
            {
                if (!ValidName(pair.Key) || pair.Value is null || pair.Value.Contains('\0'))
                { throw new IOException("invalid native environment entry"); }
                string entry = pair.Key + "=" + pair.Value;
                int length = Encoding.UTF8.GetByteCount(entry);
                if (length >= MaximumEnvironmentBytes - environmentBytes) { throw new IOException("native environment exceeds 8 KiB"); }
                environmentBytes += length + 1;
                variables.Add(entry);
            }
            variables.Sort(StringComparer.Ordinal);
        }
        byte[] bytes = new byte[StackBytes]; int cursor = bytes.Length;
        ulong[] envPointers = new ulong[variables.Count], pointers = new ulong[arguments.Count];
        for (int i = variables.Count - 1; i >= 0; i--) { envPointers[i] = Store(variables[i]); }
        int total = 0;
        for (int i = arguments.Count - 1; i >= 0; i--)
        {
            int length = Encoding.UTF8.GetByteCount(arguments[i]);
            if (length > 1024 || length >= 8192 - total || arguments[i].Contains('\0')) { throw new IOException("native argument bounds"); }
            total += length + 1;
            pointers[i] = Store(arguments[i]);
        }
        cursor = (cursor - (arguments.Count + variables.Count + 5) * 8) & ~15;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(cursor), (ulong)arguments.Count);
        for (int i = 0; i < pointers.Length; i++) { Put(cursor + 8 + i * 8, pointers[i]); }
        int envStart = cursor + (arguments.Count + 2) * 8;
        for (int i = 0; i < envPointers.Length; i++) { Put(envStart + i * 8, envPointers[i]); }
        // Zeroed argv/envp terminators and auxiliary-vector pair remain in place.
        return new(bytes, Top - StackBytes + (ulong)cursor);

        ulong Store(string text)
        {
            byte[] value = Encoding.UTF8.GetBytes(text); cursor -= value.Length + 1;
            value.CopyTo(bytes, cursor); return Top - StackBytes + (ulong)cursor;
        }
        void Put(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    }

    public static bool ValidName(string name)
    {
        if (name.Length == 0 || !Letter(name[0])) { return false; }
        foreach (char value in name) { if (!Letter(value) && (value < '0' || value > '9')) { return false; } }
        return true;
    }
    private static bool Letter(char value) => value == '_' || value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
