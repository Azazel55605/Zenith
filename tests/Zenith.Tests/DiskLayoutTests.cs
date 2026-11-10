using System;
using System.Linq;
using Zenith.Core.Storage;

namespace Zenith.Tests;

public class DiskLayoutTests
{
    private const ulong MiB = 2048, GiB = 1024 * MiB;
    private static readonly Guid Linux = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    // The installer's layout on a 2 GiB disk: 128 MiB ESP at 1 MiB, root up to the last usable sector.
    private static readonly DiskPartition Esp = new(0, MiB, 129 * MiB - 1, Guid.Empty);

    [Fact]
    public void FreeSpaceIsAlignedAndKeepsClearOfTheBackupTable()
    {
        ulong disk = 4 * GiB;
        var root = new DiskPartition(1, 129 * MiB, 129 * MiB + GiB - 1, Linux);
        var free = DiskLayout.Free(disk, new[] { Esp, root });
        var region = Assert.Single(free);
        Assert.Equal(129 * MiB + GiB, region.Start);
        Assert.Equal(0UL, region.Start % DiskLayout.Alignment);
        Assert.True(region.End <= DiskLayout.LastUsable(disk));
        Assert.Equal(0UL, region.Sectors % DiskLayout.Alignment);
    }

    [Fact]
    public void PlacementUsesTheFirstFitOrTheLargestRegion()
    {
        ulong disk = 10 * GiB;
        var a = new DiskPartition(1, 129 * MiB, 129 * MiB + GiB - 1, Linux);
        var b = new DiskPartition(2, 129 * MiB + 2 * GiB, 129 * MiB + 3 * GiB - 1, Linux);   // leaves a 1 GiB gap
        var parts = new[] { Esp, a, b };
        Assert.Equal(129 * MiB + GiB, DiskLayout.Place(disk, parts, 512 * MiB)!.Value.Start);
        Assert.Equal(129 * MiB + 3 * GiB, DiskLayout.Place(disk, parts, 2 * GiB)!.Value.Start);
        var rest = DiskLayout.Place(disk, parts, null)!.Value;
        Assert.Equal(129 * MiB + 3 * GiB, rest.Start);
        Assert.Null(DiskLayout.Place(disk, parts, 20 * GiB));
    }

    [Fact]
    public void GrowStopsAtTheNextPartitionOrTheDiskEnd()
    {
        ulong disk = 10 * GiB;
        var a = new DiskPartition(1, 129 * MiB, 129 * MiB + GiB - 1, Linux);
        var b = new DiskPartition(2, 129 * MiB + 2 * GiB, 129 * MiB + 3 * GiB - 1, Linux);
        Assert.Equal(2 * GiB, DiskLayout.GrowLimit(disk, new[] { Esp, a, b }, a));
        ulong last = DiskLayout.GrowLimit(disk, new[] { Esp, a, b }, b);
        Assert.True(b.Start + last - 1 <= DiskLayout.LastUsable(disk));
        Assert.Equal(0UL, last % DiskLayout.Alignment);
    }

    [Theory]
    [InlineData("512M", 512 * MiB)] [InlineData("1G", GiB)] [InlineData("1.5G", 1536 * MiB)]
    [InlineData("2T", 2 * 1024 * GiB)] [InlineData("1048576K", GiB)]
    public void SizesAreWholeMebibytes(string text, ulong sectors) => Assert.Equal(sectors, DiskLayout.ParseSize(text));

    [Theory]
    [InlineData("")] [InlineData("0")] [InlineData("-1G")] [InlineData("12Gi")] [InlineData("100K")] [InlineData("G")]
    public void MalformedSizesAreRefused(string text) => Assert.Null(DiskLayout.ParseSize(text));
}
