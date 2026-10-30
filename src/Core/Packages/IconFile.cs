using System;

namespace Zenith.Core.Packages;

/// <summary>Bounded reader for system icons: ZIC1, count, then u16 size, u32 bytes, runs per size.</summary>
public static class IconFile
{
    public const int MaximumBytes = 16384;
    public static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 31) { return false; }
        foreach (char c in name) { if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) { return false; } }
        return true;
    }
    /// <summary>Finds a size's run stream after validating the whole file's structure.</summary>
    public static bool TryFind(ReadOnlySpan<byte> file, int size, out ReadOnlySpan<byte> runs)
    {
        runs = default;
        if (file.Length is < 5 or > MaximumBytes || file[0] != 'Z' || file[1] != 'I' || file[2] != 'C' || file[3] != '1') { return false; }
        int at = 5; bool found = false; ReadOnlySpan<byte> match = default;
        for (int i = 0; i < file[4]; i++)
        {
            if (file.Length - at < 6) { return false; }
            int entry = file[at] | file[at + 1] << 8;
            uint bytes = (uint)(file[at + 2] | file[at + 3] << 8 | file[at + 4] << 16 | file[at + 5] << 24);
            if (entry is < 1 or > 64 || bytes > file.Length - at - 6) { return false; }
            if (entry == size) { match = file.Slice(at + 6, (int)bytes); found = true; }
            at += 6 + (int)bytes;
        }
        runs = match;
        return found && at == file.Length;
    }
    /// <summary>Expands (zero run, literal count, literals...) into size*size coverage bytes.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> runs, int size, Span<byte> output)
    {
        int total = size * size, at = 0, written = 0;
        if (output.Length < total) { return false; }
        output[..total].Clear();
        while (at + 2 <= runs.Length)
        {
            written += runs[at]; int literals = runs[at + 1]; at += 2;
            if (literals > runs.Length - at || literals > total - written) { return false; }
            runs.Slice(at, literals).CopyTo(output[written..]); written += literals; at += literals;
        }
        return at == runs.Length && written == total;
    }
}
