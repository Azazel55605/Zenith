using System.IO;
using Zenith.Core.Storage;

namespace Zenith.Tests;

public class FileSystemProbeTests
{
    [Fact]
    public void ProbeUsesIsolatedScratchDirectoryAndCleansUp()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("keep"), "untouched");
        string report = FileSystemProbe.Run(sh.PathOf(""));
        Assert.Contains("final-close deletion and reuse passed", report);
        Assert.Single(Directory.GetFileSystemEntries(sh.PathOf("")));
        Assert.Equal("untouched", File.ReadAllText(sh.PathOf("keep")));
    }
}
