using System.IO;

namespace Zenith.Tests;

public class FileCommandTests
{
    [Fact]
    public void Mkdir_RequiresParentsUnlessP()
    {
        using var sh = new ShellHarness();
        Assert.Contains("No such file or directory", sh.Run("mkdir x/y"));
        sh.Run("mkdir -p x/y");
        Assert.True(Directory.Exists(sh.PathOf("x/y")));
    }

    [Fact]
    public void Touch_CreatesEmptyFile()
    {
        using var sh = new ShellHarness();
        sh.Run("touch new.txt");
        Assert.Equal(0, new FileInfo(sh.PathOf("new.txt")).Length);
    }

    [Fact]
    public void Cp_CopiesFilesAndDirectoriesWithR()
    {
        using var sh = new ShellHarness();
        sh.Run("mkdir -p src/sub; echo hi > src/sub/f.txt");

        Assert.Contains("-r not specified", sh.Run("cp src dst"));
        sh.Run("cp -r src dst");
        Assert.Equal("hi\n", File.ReadAllText(sh.PathOf("dst/sub/f.txt")));
        Assert.Contains("into itself", sh.Run("cp -r src src/sub"));
    }

    [Fact]
    public void Mv_RenamesAndMovesIntoDirectory()
    {
        using var sh = new ShellHarness();
        sh.Run("echo a > a.txt; mkdir d");
        sh.Run("mv a.txt b.txt");
        sh.Run("mv b.txt d");
        Assert.True(File.Exists(sh.PathOf("d/b.txt")));
        Assert.False(File.Exists(sh.PathOf("a.txt")));
    }

    [Fact]
    public void Rm_NeedsRForDirectoriesAndFIgnoresMissing()
    {
        using var sh = new ShellHarness();
        sh.Run("mkdir -p d/e");
        Assert.Contains("Is a directory", sh.Run("rm d"));
        Assert.Equal(string.Empty, sh.Run("rm -f missing"));
        sh.Run("rm -r d");
        Assert.False(Directory.Exists(sh.PathOf("d")));
    }

    [Fact]
    public void Rmdir_RefusesNonEmpty()
    {
        using var sh = new ShellHarness();
        sh.Run("mkdir -p d/e");
        Assert.Contains("not empty", sh.Run("rmdir d"));
        sh.Run("rmdir d/e");
        Assert.False(Directory.Exists(sh.PathOf("d/e")));
    }

    [Fact]
    public void Ls_OnePerLineSortedHidingDotfiles()
    {
        using var sh = new ShellHarness();
        sh.Run("touch b a .hidden; mkdir c");
        Assert.Equal("a\nb\nc\n", sh.Run("ls -1"));
        Assert.Contains(".hidden", sh.Run("ls -1a"));
    }

    [Fact]
    public void Ls_LongFormatShowsTypeAndSize()
    {
        using var sh = new ShellHarness();
        sh.Run("echo 12345 > f; mkdir d");
        string listing = sh.Run("ls -l");
        Assert.Contains("drwxr-xr-x", listing);
        Assert.Matches(@"-rw-r--r-- user users\s+6 .* f", listing);
    }

    [Fact]
    public void Find_FiltersByNameAndType()
    {
        using var sh = new ShellHarness();
        sh.Run("mkdir -p a/b; touch a/x.txt a/b/y.txt a/z.md");
        Assert.Equal("a/b/y.txt\na/x.txt\n", sh.Run("find a -name '*.txt'"));
        Assert.Equal("a\na/b\n", sh.Run("find a -type d"));
    }

    [Fact]
    public void Tree_CountsDirectoriesAndFiles()
    {
        using var sh = new ShellHarness();
        sh.Run("mkdir -p a/b; touch a/x a/b/y");
        Assert.EndsWith("2 directories, 2 files\n", sh.Run("tree ."));
    }
}

public class TextCommandTests
{
    private static ShellHarness WithFile(string text)
    {
        var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("f.txt"), text);
        return sh;
    }

    [Fact]
    public void Cat_ConcatenatesFilesAndStdin()
    {
        using var sh = WithFile("one\n");
        Assert.Equal("one\nin\n", sh.Run("echo in | cat f.txt -"));
        Assert.Contains("No such file", sh.Run("cat nope.txt"));
    }

    [Fact]
    public void HeadAndTail()
    {
        using var sh = WithFile("1\n2\n3\n4\n5\n");
        Assert.Equal("1\n2\n", sh.Run("head -n 2 f.txt"));
        Assert.Equal("4\n5\n", sh.Run("tail -2 f.txt"));
    }

    [Fact]
    public void Grep_Flags()
    {
        using var sh = WithFile("Apple\nbanana\ncherry\n");
        Assert.Equal("Apple\n", sh.Run("grep -i apple f.txt"));
        Assert.Equal("2:banana\n", sh.Run("grep -n ban f.txt"));
        Assert.Equal("Apple\ncherry\n", sh.Run("grep -v an f.txt"));
        sh.Run("grep zzz f.txt");
        Assert.Equal(1, sh.Shell.LastStatus);
    }

    [Fact]
    public void Wc_CountsLinesWordsBytes()
    {
        using var sh = WithFile("a b\nc\n");
        Assert.Equal("      2       3       6 f.txt\n", sh.Run("wc f.txt"));
        Assert.Equal("      2\n", sh.Run("cat f.txt | wc -l"));
    }

    [Fact]
    public void SortAndUniq()
    {
        using var sh = WithFile("b\na\nb\nb\nc\n");
        Assert.Equal("a\nb\nb\nb\nc\n", sh.Run("sort f.txt"));
        Assert.Equal("c\nb\na\n", sh.Run("sort -r f.txt | uniq"));
    }

    [Fact]
    public void Tee_WritesFileAndPassesThrough()
    {
        using var sh = new ShellHarness();
        Assert.Equal("x\n", sh.Run("echo x | tee copy.txt"));
        Assert.Equal("x\n", File.ReadAllText(sh.PathOf("copy.txt")));
    }
}
