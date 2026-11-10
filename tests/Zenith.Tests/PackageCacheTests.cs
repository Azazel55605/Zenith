using System.Linq;
using Zenith.Core.Packages;

namespace Zenith.Tests;

public class PackageCacheTests
{
    private static readonly string Digest = new string('a', 63) + "f";
    private static PackageCacheEntry File(string name, ulong bytes = 100, long modified = 0, bool symlink = false, bool regular = true)
        => new(name, regular, symlink, bytes, modified);

    [Fact]
    public void CleanRemovesOnlyArchivesAndStagesAndKeepsIndexes()
    {
        var plan = PackageCachePolicy.Plan(new[]
        {
            File(Digest + ".zpk", 17_000_000), File("core-v3.zri"), File("core-v3.zri.incoming", 40), File("core-v3.zri.pkg-new", 50),
            File("notes.txt"), File(new string('b', 64) + ".zpk", symlink: true), File(new string('c', 64) + ".zpk", regular: false),
            File("ABC" + new string('d', 61) + ".zpk"), File("extra.zri"),
        }, PackageCacheMode.All, 1000);
        Assert.Equal(new[] { Digest + ".zpk", "core-v3.zri.incoming", "core-v3.zri.pkg-new" }, plan.Remove.Select(e => e.Name));
        Assert.Equal(17_000_090UL, plan.Bytes);
        // Linked, non-regular and unknown entries stay and are reported; signed indexes stay silently.
        Assert.Equal(4, plan.Kept.Count);
        Assert.DoesNotContain(plan.Kept, e => e.Name.EndsWith(".zri"));
    }

    [Fact]
    public void PeriodicCleanupWaitsForArchiveAndStageAges()
    {
        long now = 10 * PackageCachePolicy.ArchiveAgeSeconds;
        var entries = new[]
        {
            File(Digest + ".zpk", modified: now - PackageCachePolicy.ArchiveAgeSeconds + 1),
            File(new string('e', 64) + ".zpk", modified: now - PackageCachePolicy.ArchiveAgeSeconds),
            File("core-v3.zri.incoming", modified: now - PackageCachePolicy.StageAgeSeconds + 1),
            File("core-v3.zri.pkg-new", modified: now - PackageCachePolicy.StageAgeSeconds),
        };
        var plan = PackageCachePolicy.Plan(entries, PackageCacheMode.Aged, now);
        Assert.Equal(new[] { new string('e', 64) + ".zpk", "core-v3.zri.pkg-new" }, plan.Remove.Select(e => e.Name));
        Assert.Empty(plan.Kept);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("ZENITH-PKG-CACHE 1 auto=on\n", true)]
    [InlineData("ZENITH-PKG-CACHE 1 auto=off\n", false)]
    [InlineData("garbage", true)]
    public void AutomaticCleanupIsOnUnlessExplicitlyOff(string? text, bool expected)
    {
        Assert.Equal(expected, PackageCachePolicy.AutomaticEnabled(text));
        Assert.Equal(expected, PackageCachePolicy.AutomaticEnabled(PackageCachePolicy.Config(expected)));
    }

    [Fact]
    public void DownloadsNeedRoomForArchivesPayloadsAndReserve()
        => Assert.Equal(2 * 17_000_000UL + PackageCachePolicy.ReserveBytes, PackageCachePolicy.Required(17_000_000));
}
