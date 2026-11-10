using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Core.Storage;

/// <summary>A GPT partition by sector range, inclusive end; Index is its position in the table.</summary>
internal readonly record struct DiskPartition(int Index, ulong Start, ulong End, Guid Type)
{
    public ulong Sectors => End - Start + 1;
}

/// <summary>Free sectors between partitions, inclusive.</summary>
internal readonly record struct DiskRegion(ulong Start, ulong End)
{
    public ulong Sectors => End - Start + 1;
}

/// <summary>
/// Where partitions may go on a GPT disk of 512-byte sectors: starts on 1 MiB boundaries, lengths in
/// whole MiB, nothing in the first MiB or the last 34 sectors (backup entries and header, which
/// GptChecksums rewrites at the real end of an enlarged disk). Pure; the disk commands apply it.
/// </summary>
internal static class DiskLayout
{
    public const ulong Alignment = 2048, Reserved = 34, MinimumSectors = 2048;

    public static ulong LastUsable(ulong sectors) => sectors - Reserved - 1;

    public static List<DiskRegion> Free(ulong sectors, IReadOnlyList<DiskPartition> partitions)
    {
        var used = new List<DiskPartition>(partitions);
        used.Sort((a, b) => a.Start.CompareTo(b.Start));
        var free = new List<DiskRegion>();
        ulong cursor = Alignment, last = LastUsable(sectors);
        foreach (var p in used)
        {
            Add(free, cursor, Math.Min(p.Start == 0 ? 0 : p.Start - 1, last));
            cursor = Math.Max(cursor, Align(p.End + 1));
        }
        Add(free, cursor, last);
        return free;
    }

    private static void Add(List<DiskRegion> free, ulong start, ulong end)
    {
        if (end < start) { return; }
        ulong length = (end - start + 1) / Alignment * Alignment;
        if (length >= MinimumSectors) { free.Add(new(start, start + length - 1)); }
    }

    private static ulong Align(ulong sector) => (sector + Alignment - 1) / Alignment * Alignment;

    /// <summary>Where a new partition of <paramref name="sectors"/> (null: the largest free region) goes:
    /// the first free region it fits in. Null when none.</summary>
    public static DiskRegion? Place(ulong diskSectors, IReadOnlyList<DiskPartition> partitions, ulong? sectors)
    {
        DiskRegion? best = null;
        foreach (var region in Free(diskSectors, partitions))
        {
            if (sectors is ulong wanted)
            {
                if (region.Sectors >= wanted) { return new DiskRegion(region.Start, region.Start + wanted - 1); }
            }
            else if (best is null || region.Sectors > best.Value.Sectors) { best = region; }
        }
        return sectors is null ? best : null;
    }

    /// <summary>The largest length (whole MiB) <paramref name="target"/> can grow to in place: up to the next
    /// partition or the last usable sector.</summary>
    public static ulong GrowLimit(ulong diskSectors, IReadOnlyList<DiskPartition> partitions, DiskPartition target)
    {
        ulong end = LastUsable(diskSectors);
        foreach (var p in partitions)
        {
            if (p.Index != target.Index && p.Start > target.End && p.Start - 1 < end) { end = p.Start - 1; }
        }
        if (end < target.End) { return target.Sectors; }
        ulong length = (end - target.Start + 1) / Alignment * Alignment;
        return Math.Max(length, target.Sectors);
    }

    /// <summary>Sizes: <c>NNN</c> bytes, or with K, M, G, T (binary) suffixes; whole MiB, at least 1 MiB.
    /// Returns sectors, or null when malformed.</summary>
    public static ulong? ParseSize(string text)
    {
        if (string.IsNullOrEmpty(text)) { return null; }
        char last = char.ToUpperInvariant(text[^1]);
        int shift = last switch { 'K' => 10, 'M' => 20, 'G' => 30, 'T' => 40, _ => 0 };
        string digits = shift == 0 ? text : text[..^1];
        if (digits.EndsWith("i", StringComparison.OrdinalIgnoreCase)) { return null; }
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) || value <= 0) { return null; }
        decimal bytes = value * (decimal)Math.Pow(2, shift);
        if (bytes > 1UL << 50) { return null; }
        ulong sectors = (ulong)(bytes / 512) / Alignment * Alignment;
        return sectors >= MinimumSectors ? sectors : null;
    }

    public static string Human(ulong bytes)
        => bytes >= 1UL << 40 ? (bytes / (double)(1UL << 40)).ToString("0.0", CultureInfo.InvariantCulture) + " TiB"
            : bytes >= 1UL << 30 ? (bytes / (double)(1UL << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
            : bytes >= 1UL << 20 ? (bytes / (double)(1UL << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
            : (bytes >> 10) + " KiB";
}
