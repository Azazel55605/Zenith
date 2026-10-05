using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Filesystems.Fat;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;

namespace Zenith.Core.Storage;

/// <summary>
/// Installs Zenith onto a disk:
/// <list type="number">
/// <item>a fresh GPT with an EFI System Partition (64 MiB, FAT32) and a root partition (the rest, FAT32);</item>
/// <item>the Unix layout plus the current <c>/etc</c>, <c>/home</c>, <c>/root</c>, <c>/usr</c>, <c>/var</c> copied onto the root;</item>
/// <item><c>limine.conf</c> written to the ESP.</item>
/// </list>
/// The bootloader binary and the kernel ELF cannot be copied from here yet (the kernel has no
/// way to read its own boot medium), so <c>tools/make-bootable.sh</c> adds them from the host.
/// On the next boot <see cref="SystemMounts"/> finds the root partition by its <c>/etc/os-release</c>.
/// </summary>
internal static class Installer
{
    public const ulong MinimumBytes = 128UL * 1024 * 1024;

    private const ulong EspBytes = 64UL * 1024 * 1024;
    private const ulong Alignment = 2048;   // 1 MiB in 512-byte sectors
    private const string TargetMount = "/mnt/target";
    private const string EspMount = "/mnt/esp";

    /// <summary>GPT type of an EFI System Partition: C12A7328-F81F-11D2-BA4B-00A0C93EC93B.</summary>
    private static readonly Guid EfiSystemPartitionType =
        new(0xC12A7328, 0xF81F, 0x11D2, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B);

    private static readonly string[] s_copiedTrees = { "/etc", "/home", "/root", "/usr", "/var" };

    /// <summary>Disks Zenith could be installed on: big enough, and not the disk the running root lives on.</summary>
    public static List<IBlockDevice> Candidates()
    {
        var disks = new List<IBlockDevice>();
        foreach (IBlockDevice disk in StorageManager.Devices)
        {
            if (disk.BlockCount * disk.BlockSize >= MinimumBytes && !ReferenceEquals(disk, SystemMounts.RootPartition?.Host))
            {
                disks.Add(disk);
            }
        }

        return disks;
    }

    /// <summary>Erases <paramref name="disk"/> and installs onto it. Throws on failure.</summary>
    public static void Install(IBlockDevice disk, Action<string> progress)
    {
        if (disk.BlockSize != 512)
        {
            throw new IOException("only 512-byte sector disks are supported");
        }

        if (ReferenceEquals(disk, SystemMounts.RootPartition?.Host))
        {
            throw new IOException("refusing to install over the running system's disk");
        }

        ulong espSectors = EspBytes / disk.BlockSize;
        ulong rootStart = Alignment + espSectors;
        ulong rootSectors = (disk.BlockCount - 34 - rootStart) / Alignment * Alignment;   // keep clear of the backup GPT

        progress("Writing partition table (GPT)");
        Gpt.Create(disk);
        if (!Gpt.AddPartition(disk, Alignment, espSectors, EfiSystemPartitionType)
            || !Gpt.AddPartition(disk, rootStart, rootSectors, Gpt.BasicDataPartitionType))
        {
            throw new IOException("could not create partitions");
        }

        GptChecksums.Complete(disk);

        StorageManager.RescanPartitions(disk);
        IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(disk);
        if (partitions.Count != 2)
        {
            throw new IOException("expected 2 partitions after rescan, found " + partitions.Count);
        }

        Partition esp = partitions[0], root = partitions[1];

        progress("Formatting EFI system partition (FAT32, 64 MiB)");
        Format(esp, "EFI        ");
        progress("Formatting root partition (FAT32, " + (rootSectors * disk.BlockSize / (1024 * 1024)) + " MiB)");
        Format(root, "ZENITH     ");

        Directory.CreateDirectory(TargetMount);
        Directory.CreateDirectory(EspMount);
        Mount(root, TargetMount);
        try
        {
            progress("Creating directory layout");
            FileSystemLayout.Create(TargetMount);

            foreach (string tree in s_copiedTrees)
            {
                if (Directory.Exists(tree))
                {
                    progress("Copying " + tree);
                    Shell.Commands.FileCommands.CopyDirectory(tree, TargetMount + tree);
                }
            }

            File.WriteAllText(TargetMount + "/etc/fstab",
                "# <device>  <mount point>  <type>  <options>\n" +
                "LABEL=ZENITH /              fat     defaults\n" +
                "LABEL=EFI   /boot/efi      fat     defaults\n" +
                "tmpfs       /tmp           tmpfs   defaults\n");
        }
        finally
        {
            VfsManager.TryUnmount(TargetMount);
        }

        Mount(esp, EspMount);
        try
        {
            progress("Writing bootloader configuration");
            Directory.CreateDirectory(EspMount + "/EFI/BOOT");
            Directory.CreateDirectory(EspMount + "/boot");
            File.WriteAllText(EspMount + "/limine.conf",
                "timeout: 0\n\n/Zenith\n    protocol: limine\n    path: boot():/boot/Zenith.elf\n");
        }
        finally
        {
            VfsManager.TryUnmount(EspMount);
        }

        disk.Flush();
        progress("Done");
    }

    private static void Format(Partition partition, string label)
    {
        var options = new FatFormatOptions { Type = FatType.Fat32, VolumeLabel = label };
        if (!VfsManager.TryFormat(SystemMounts.Fat, partition, options))
        {
            throw new IOException("could not format " + partition.Name);
        }
    }

    private static void Mount(Partition partition, string mountPoint)
    {
        if (!VfsManager.TryMount(SystemMounts.Fat, partition, MountFlags.None, mountPoint, out _))
        {
            throw new IOException("could not mount " + partition.Name + " at " + mountPoint);
        }
    }
}
