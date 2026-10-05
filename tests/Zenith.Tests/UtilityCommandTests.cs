using System.IO;

namespace Zenith.Tests;

public class UtilityCommandTests
{
    [Theory]
    [InlineData("seq 3", "1\n2\n3\n")]
    [InlineData("seq 2 4", "2\n3\n4\n")]
    [InlineData("seq 10 -5 0", "10\n5\n0\n")]
    [InlineData("seq 3 1", "")]
    [InlineData("printf '%s=%d\\n' a 1 b 2", "a=1\nb=2\n")]
    [InlineData("printf 'no newline'", "no newline")]
    [InlineData("printf '100%%\\n'", "100%\n")]
    [InlineData("basename /a/b/c.txt", "c.txt\n")]
    [InlineData("basename /a/b/c.txt .txt", "c\n")]
    [InlineData("basename /a/b/", "b\n")]
    [InlineData("dirname /a/b/c.txt", "/a/b\n")]
    [InlineData("dirname c.txt", ".\n")]
    [InlineData("dirname /c", "/\n")]
    [InlineData("echo hello | tr a-z A-Z", "HELLO\n")]
    [InlineData("echo hello | tr -d l", "heo\n")]
    [InlineData("echo 'a:b:c' | cut -d: -f2", "b\n")]
    [InlineData("echo 'a:b:c' | cut -d : -f 1,3", "a:c\n")]
    [InlineData("echo abcdef | cut -c2-4", "bcd\n")]
    [InlineData("echo nodelim | cut -d: -f2", "nodelim\n")]
    [InlineData("echo a b c | xargs echo got", "got a b c\n")]
    [InlineData("seq 3 | xargs", "1 2 3\n")]
    public void Command(string line, string expected)
    {
        using var sh = new ShellHarness();
        Assert.Equal(expected, sh.Run(line));
    }

    [Theory]
    [InlineData("echo hello world | sed s/o/0/", "hell0 world\n")]
    [InlineData("echo hello world | sed s/o/0/g", "hell0 w0rld\n")]
    [InlineData("echo 'a-1 b-2' | sed 's/\\([a-z]\\)-\\([0-9]\\)/\\2\\1/g'", "a-1 b-2\n")]   // basic-regex groups are not supported
    [InlineData("echo 'a-1 b-2' | sed -E 's/([a-z])-([0-9])/\\2\\1/g'", "")]               // -E is not a sed flag here
    [InlineData("echo 'a-1 b-2' | sed 's/([a-z])-([0-9])/\\2\\1/g'", "1a 2b\n")]           // .NET regex syntax
    [InlineData("echo abc | sed 's/b/[&]/'", "a[b]c\n")]
    [InlineData("seq 5 | sed 2d", "1\n3\n4\n5\n")]
    [InlineData("seq 5 | sed -n 3p", "3\n")]
    [InlineData("seq 12 | sed '/1/d'", "2\n3\n4\n5\n6\n7\n8\n9\n")]
    [InlineData("seq 3 | sed 's/2/two/;1d'", "two\n3\n")]
    public void Sed(string line, string expected)
    {
        using var sh = new ShellHarness();
        string output = sh.Run(line);
        if (expected.Length == 0)
        {
            Assert.NotEqual(0, sh.Shell.LastStatus);
        }
        else
        {
            Assert.Equal(expected, output);
        }
    }

    [Fact]
    public void GrepE_UsesRegularExpressions()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("f.txt"), "cat\ncot\ncut\ndog\n");
        Assert.Equal("cat\ncot\n", sh.Run("grep -E 'c[ao]t' f.txt"));
        Assert.Equal("dog\n", sh.Run("grep -vE '^c' f.txt"));
        Assert.Contains("invalid regular expression", sh.Run("grep -E '(' f.txt"));
    }

    [Fact]
    public void Du_SumsDirectories()
    {
        using var sh = new ShellHarness();
        Directory.CreateDirectory(sh.PathOf("d/sub"));
        File.WriteAllText(sh.PathOf("d/a"), new string('x', 3000));
        File.WriteAllText(sh.PathOf("d/sub/b"), new string('x', 2000));

        Assert.Equal("5\td\n", sh.Run("du -s d"));
        Assert.Equal("2\td/sub\n5\td\n", sh.Run("du d"));
    }

    [Fact]
    public void Diff_ReportsHunks()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("a"), "one\ntwo\nthree\nfour\n");
        File.WriteAllText(sh.PathOf("b"), "one\n2\nthree\nfour\nfive\n");

        Assert.Equal("2c2\n< two\n---\n> 2\n4a5\n> five\n", sh.Run("diff a b"));
        Assert.Equal(1, sh.Shell.LastStatus);
        Assert.Equal(string.Empty, sh.Run("diff a a"));
        Assert.Equal(0, sh.Shell.LastStatus);

        File.WriteAllText(sh.PathOf("c"), "one\nfour\n");
        Assert.Equal("2,3d1\n< two\n< three\n", sh.Run("diff a c"));
    }
}
