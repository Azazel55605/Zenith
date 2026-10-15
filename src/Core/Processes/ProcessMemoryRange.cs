using System.Collections.Generic;

namespace Zenith.Core.Processes;

internal readonly record struct ProcessMemoryRange(ulong Start, ulong End, bool Writable)
{
    public static bool Allows(IReadOnlyList<ProcessMemoryRange> ranges, ulong address, ulong length, bool write)
    {
        if (length == 0) { return true; }
        if (address + length < address) { return false; }
        ulong end = address + length;
        while (address < end)
        {
            ulong next = address;
            foreach (var range in ranges)
            {
                if (address >= range.Start && address < range.End && (!write || range.Writable))
                { next = range.End < end ? range.End : end; break; }
            }
            if (next == address) { return false; }
            address = next;
        }
        return true;
    }
}
