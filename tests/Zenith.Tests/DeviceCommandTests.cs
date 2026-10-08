using System.IO;

namespace Zenith.Tests;

public class DeviceCommandTests
{
    [Fact]
    public void DdCopiesBinaryBlocksAndStopsAtCount()
    {
        using var h = new ShellHarness();
        byte[] input = { 0, 255, 128, 1, 2, 3, 4 };
        File.WriteAllBytes(h.PathOf("in"), input);
        h.Run("dd if=in of=out bs=2 count=2");
        Assert.Equal(0, h.Shell.LastStatus);
        Assert.Equal(new byte[] { 0, 255, 128, 1 }, File.ReadAllBytes(h.PathOf("out")));
    }

    [Fact]
    public void DdStopsAtEofAndPreservesPartialFinalBlock()
    {
        using var h = new ShellHarness();
        File.WriteAllBytes(h.PathOf("in"), new byte[] { 0, 255, 3 });
        h.Run("dd if=in of=out bs=2 count=100");
        Assert.Equal(0, h.Shell.LastStatus);
        Assert.Equal(File.ReadAllBytes(h.PathOf("in")), File.ReadAllBytes(h.PathOf("out")));
        h.Run("dd if=in of=out count=0");
        Assert.Equal(0, h.Shell.LastStatus);
        Assert.Empty(File.ReadAllBytes(h.PathOf("out")));
    }

    [Theory]
    [InlineData("bs=0 count=1")]
    [InlineData("bs=1048577 count=1")]
    [InlineData("bs=two count=1")]
    [InlineData("count=-1")]
    [InlineData("count=9223372036854775808")]
    [InlineData("count=1 skip=1")]
    [InlineData("count=1 nonsense")]
    [InlineData("bs=1")]
    public void DdRejectsBadOperandsBeforeTruncatingOutput(string args)
    {
        using var h = new ShellHarness();
        File.WriteAllText(h.PathOf("in"), "source");
        File.WriteAllText(h.PathOf("out"), "keep");
        h.Run("dd if=in of=out " + args);
        Assert.Equal(1, h.Shell.LastStatus);
        Assert.Equal("keep", File.ReadAllText(h.PathOf("out")));
    }

    [Fact]
    public void DdRejectsSamePathAndMissingInputWithoutDataLoss()
    {
        using var h = new ShellHarness();
        File.WriteAllText(h.PathOf("out"), "keep");
        h.Run("dd if=out of=./out count=1");
        Assert.Equal(1, h.Shell.LastStatus);
        Assert.Equal("keep", File.ReadAllText(h.PathOf("out")));
        h.Run("dd if=out of=OUT count=1");
        Assert.Equal(1, h.Shell.LastStatus);
        Assert.Equal("keep", File.ReadAllText(h.PathOf("out")));
        h.Run("dd if=missing of=out count=1");
        Assert.Equal(1, h.Shell.LastStatus);
        Assert.Equal("keep", File.ReadAllText(h.PathOf("out")));
    }
}
