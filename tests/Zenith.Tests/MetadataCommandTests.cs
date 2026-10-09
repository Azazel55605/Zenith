using System.Collections.Generic;
using System.IO;
using Zenith.Core.Shell;

namespace Zenith.Tests;

public class MetadataCommandTests
{
    private sealed class Backend : IFileMetadata
    {
        public Dictionary<string, FileMetadata> Files = new();
        public bool TryRead(string path, out FileMetadata metadata) => Files.TryGetValue(Path.GetFileName(path), out metadata);
        public bool SetMode(string path, ushort mode)
        {
            string name = Path.GetFileName(path);
            if (!Files.TryGetValue(name, out var metadata)) { return false; }
            Files[name] = metadata with { Mode = (ushort)((metadata.Mode & 0xf000) | mode) }; return true;
        }
        public bool SetOwner(string path, uint uid, uint? gid)
        {
            string name = Path.GetFileName(path);
            if (!Files.TryGetValue(name, out var metadata)) { return false; }
            Files[name] = metadata with { Uid = uid, Gid = gid ?? metadata.Gid }; return true;
        }
    }

    private static FileMetadata File(ushort mode = 0x81a4, string? target = null)
        => new(mode, 70000, 80000, 2, 6, 1000000000, 42, 2, 1024, true, target);

    [Fact]
    public void LsAndStatShowRealAttributesAndDanglingLinks()
    {
        var backend = new Backend(); backend.Files["f"] = File(); backend.Files["dangling"] = File(0xa1ff, "missing");
        using var sh = new ShellHarness(backend); sh.Run("echo 12345 > f");
        Assert.Contains("-rw-r--r-- 2 70000 80000", sh.Run("ls -l f"));
        Assert.Contains("lrwxrwxrwx 2 70000 80000", sh.Run("ls -l dangling"));
        Assert.Contains("dangling -> missing", sh.Run("ls -l dangling"));
        string stat = sh.Run("stat dangling");
        Assert.Contains("symbolic link", stat); Assert.Contains("Uid: 70000", stat);
        Assert.Contains("Inode: 42", stat); Assert.Contains("Blocks: 2", stat);
    }

    [Fact]
    public void NumericChangesWorkInChildShellAndPreserveUnselectedGroup()
    {
        var backend = new Backend(); backend.Files["f"] = File();
        using var sh = new ShellHarness(backend);
        sh.Run("chmod 4750 f; chown 123456 f");
        Assert.Equal(0x89e8, backend.Files["f"].Mode);
        Assert.Equal(123456u, backend.Files["f"].Uid); Assert.Equal(80000u, backend.Files["f"].Gid);
        System.IO.File.WriteAllText(sh.PathOf("script"), "chown 70000:80000 f\nchmod 0600 f\n");
        sh.Run("sh script");
        Assert.Equal(70000u, backend.Files["f"].Uid); Assert.Equal(0x8180, backend.Files["f"].Mode);
    }

    [Theory]
    [InlineData("chmod 888 f")] [InlineData("chmod 10000 f")] [InlineData("chmod u+x f")]
    [InlineData("chown -1 f")] [InlineData("chown 4294967296 f")] [InlineData("chown 1: f")]
    [InlineData("chown user f")] [InlineData("chown 1:2:3 f")]
    public void InvalidInputNeverMutatesAttributes(string command)
    {
        var backend = new Backend(); backend.Files["f"] = File();
        using var sh = new ShellHarness(backend); sh.Run(command);
        Assert.Equal(1, sh.Shell.LastStatus); Assert.Equal(File(), backend.Files["f"]);
    }

    [Theory]
    [InlineData(0x89e8, "-rwsr-x---")] [InlineData(0x8800, "---S------")]
    [InlineData(0x4400, "d-----S---")] [InlineData(0x4200, "d--------T")]
    [InlineData(0x43ff, "drwxrwxrwt")]
    public void SpecialBitsUseCorrectPermissionCharacters(ushort mode, string expected)
        => Assert.Equal(expected, File(mode).Permissions);
}
