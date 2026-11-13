using System;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Storage;

namespace Zenith.Core.Monitoring;

/// <summary>
/// Bytes read from and written to each disk since boot, for Observatory's Disk card. Counted where
/// Zenith moves blocks: its ext2 driver and raw <c>/dev</c> access. Partitions count toward their
/// disk; RAM disks (the live root, <c>/tmp</c>) are not disks and are left out. FAT volumes go
/// through Cosmos's own driver and are not counted yet.
/// </summary>
internal static class IoCounters
{
    public const int MaximumDisks = 8;
    private static readonly object s_gate = new();
    private static readonly string?[] s_names = new string?[MaximumDisks];
    private static readonly ulong[] s_read = new ulong[MaximumDisks], s_written = new ulong[MaximumDisks];

    public static void Disk(IBlockDevice? device, ulong bytes, bool write)
    {
        if (device is null || bytes == 0) { return; }
        IBlockDevice disk = device is Partition partition ? partition.Host : device;
        if (disk is Zenith.Core.Storage.MemoryBlockDevice) { return; }
        string name = disk.Name;
        lock (s_gate)
        {
            for (int i = 0; i < MaximumDisks; i++)
            {
                if (s_names[i] is null) { s_names[i] = name; }
                if (s_names[i] != name) { continue; }
                if (write) { s_written[i] += bytes; } else { s_read[i] += bytes; }
                return;
            }
        }
    }

    /// <summary>Totals of a disk by name (zero when it has not moved a block yet).</summary>
    public static (ulong Read, ulong Written) Of(string name)
    {
        lock (s_gate)
        {
            for (int i = 0; i < MaximumDisks; i++) { if (s_names[i] == name) { return (s_read[i], s_written[i]); } }
        }
        return (0, 0);
    }
}
