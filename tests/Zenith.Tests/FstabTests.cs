using System;
using Zenith.Core.Storage;
using Xunit;

namespace Zenith.Tests;

public class FstabTests
{
    private static readonly Guid Data = new("1b4e28ba-2fa1-11d2-883f-0016d3cca427");
    private static readonly Guid Other = new("6fa459ea-ee8a-3ca4-894e-db77e160355e");

    private const string HandWritten =
        "# my disks\n"
        + "PARTUUID=6fa459ea-ee8a-3ca4-894e-db77e160355e\t/srv/media   ext2  defaults,noatime  0  2\n"
        + "\n"
        + "UUID=1234-ABCD /mnt/usb vfat defaults 0 0\n"
        + "/dev/sata0p3 /mnt/scratch auto noauto 0 0\n";

    [Fact]
    public void UnderstandsPartuuidDeviceNamesAndAutoAndReportsTheRest()
    {
        var lines = Fstab.Parse(HandWritten);
        Assert.Equal(5, lines.Count);
        Assert.Equal(Other, lines[1].Entry!.Partition);
        Assert.Equal("/srv/media", lines[1].Entry!.MountPoint);
        Assert.Null(lines[2].Entry); Assert.Null(lines[2].Problem);
        Assert.NotNull(lines[3].Problem);   // UUID= (a filesystem UUID) is not something Zenith resolves
        Assert.Equal("sata0p3", lines[4].Entry!.Device);
        Assert.True(lines[4].Entry!.NoAuto);
        Assert.Equal(1, Fstab.Skipped(lines));
    }

    [Fact]
    public void UnchangedLinesSurviveByteForByte()
    {
        Assert.Equal(HandWritten, Fstab.Serialize(Fstab.Parse(HandWritten)));
        string crlf = "# windows editor\r\nPARTUUID=6fa459ea-ee8a-3ca4-894e-db77e160355e /mnt/a ext2 defaults 0 2\r\n";
        Assert.Equal(crlf, Fstab.Serialize(Fstab.Parse(crlf)));
        Assert.Equal("no newline at end", Fstab.Serialize(Fstab.Parse("no newline at end")).TrimEnd('\n'));
    }

    [Fact]
    public void AddingAMountAppendsOneLineAndKeepsEveryOther()
    {
        var edited = Fstab.Serialize(Fstab.With(Fstab.Parse(HandWritten), Data, "sata1p1", "/mnt/data", "ext2"));
        Assert.Equal(HandWritten + "PARTUUID=1b4e28ba-2fa1-11d2-883f-0016d3cca427 /mnt/data ext2 defaults 0 2\n", edited);
    }

    [Fact]
    public void RewritingAnExistingLineKeepsItsOptionsAndPlace()
    {
        var edited = Fstab.Serialize(Fstab.With(Fstab.Parse(HandWritten), Other, null, "/mnt/media", "ext2"));
        Assert.Contains("# my disks\nPARTUUID=6fa459ea-ee8a-3ca4-894e-db77e160355e /mnt/media ext2 defaults,noatime 0 2\n\nUUID=1234-ABCD", edited);
        // A device-name line for the same partition is rewritten too, and loses noauto (it is asked to mount now).
        var byName = Fstab.Serialize(Fstab.With(Fstab.Parse(HandWritten), Data, "sata0p3", "/mnt/scratch", "ext2"));
        Assert.EndsWith("PARTUUID=1b4e28ba-2fa1-11d2-883f-0016d3cca427 /mnt/scratch ext2 defaults 0 0\n", byName);
    }

    [Fact]
    public void RemovingTouchesOnlyThatPartition()
    {
        var edited = Fstab.Serialize(Fstab.Without(Fstab.Parse(HandWritten), Other));
        Assert.Equal(HandWritten.Replace("PARTUUID=6fa459ea-ee8a-3ca4-894e-db77e160355e\t/srv/media   ext2  defaults,noatime  0  2\n", ""), edited);
    }

    [Fact]
    public void AMountPointAnotherLineUsesIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Fstab.With(Fstab.Parse(HandWritten), Data, null, "/srv/media", "ext2"));
        var twice = Fstab.Parse("/dev/a /mnt/x ext2 defaults\n/dev/b /mnt/x ext2 defaults\n");
        Assert.NotNull(twice[0].Entry); Assert.Null(twice[1].Entry); Assert.NotNull(twice[1].Problem);
    }

    [Fact]
    public void TheInstallersSystemLinesAreKeptAndNeverReported()
    {
        const string installed = "# <device>  <mount point>  <type>  <options>\n"
            + "PARTUUID=6fa459ea-ee8a-3ca4-894e-db77e160355e / ext2 defaults\n"
            + "PARTUUID=1b4e28ba-2fa1-11d2-883f-0016d3cca427 /boot/efi fat defaults\n"
            + "tmpfs /tmp tmpfs defaults\n";
        var lines = Fstab.Parse(installed);
        Assert.Equal(0, Fstab.Skipped(lines));
        Assert.True(lines[1].Entry!.System && lines[3].Entry!.System);
        // The root's own partition is never "mounted at startup" by a line Zenith manages, and
        // adding a data partition leaves the system lines alone.
        Assert.Null(Fstab.Find(lines, Other));
        var data = new Guid("0d5a1c9e-7b3f-4c1d-9a2b-3c4d5e6f7a8b");
        Assert.Equal(installed + "PARTUUID=0d5a1c9e-7b3f-4c1d-9a2b-3c4d5e6f7a8b /mnt/data ext2 defaults 0 2\n",
            Fstab.Serialize(Fstab.With(lines, data, "sata1p0", "/mnt/data", "ext2")));
        Assert.Equal(installed, Fstab.Serialize(Fstab.Without(Fstab.With(lines, data, null, "/mnt/data", "ext2"), data)));
    }

    [Fact]
    public void NamesAndSystemMountPoints()
    {
        Assert.Null(Fstab.CheckName("data-2"));
        Assert.NotNull(Fstab.CheckName(".hidden")); Assert.NotNull(Fstab.CheckName("a/b")); Assert.NotNull(Fstab.CheckName(""));
        Assert.NotNull(Fstab.CheckName("sixteen-chars-xx"));
        Assert.True(Fstab.IsSystem("/")); Assert.True(Fstab.IsSystem("/boot")); Assert.False(Fstab.IsSystem("/mnt/data"));
        Assert.Equal("/mnt/data", Fstab.PointFor("data"));
    }
}
