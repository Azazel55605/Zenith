using System.IO;

namespace Zenith.Tests;

public class ShellTests
{
    [Fact]
    public void Pipeline_PassesOutputAlong()
    {
        using var sh = new ShellHarness();
        Assert.Equal("2\n", sh.Run("echo one two | wc -w").TrimStart());
    }

    [Fact]
    public void Redirection_WritesAppendsAndReads()
    {
        using var sh = new ShellHarness();
        sh.Run("echo first > out.txt");
        sh.Run("echo second >> out.txt");

        Assert.Equal("first\nsecond\n", File.ReadAllText(sh.PathOf("out.txt")));
        Assert.Equal("second\n", sh.Run("grep sec < out.txt"));
    }

    [Fact]
    public void Redirection_StripsColors()
    {
        using var sh = new ShellHarness();
        Directory.CreateDirectory(sh.PathOf("dir"));
        sh.Run("ls > listing.txt");

        Assert.Equal("dir\n", File.ReadAllText(sh.PathOf("listing.txt")));
    }

    [Fact]
    public void Pipes_CarryPlainTextAndLsGoesOnePerLine()
    {
        using var sh = new ShellHarness();
        sh.Run("mkdir alpha beta; touch gamma");
        Assert.Equal("alpha\nbeta\ngamma\n", sh.Run("ls | cat"));
        Assert.Equal("beta\n", sh.Run("ls | grep et | cat"));
    }

    [Fact]
    public void ExitStatus_IsExpandedPerCommand()
    {
        using var sh = new ShellHarness();
        Assert.Contains("status=127", sh.Run("nope; echo status=$?"));
        Assert.Equal("1\n", sh.Run("false; echo $?"));
    }

    [Fact]
    public void AndOr_ShortCircuit()
    {
        using var sh = new ShellHarness();
        Assert.Equal("b\n", sh.Run("false && echo a || echo b"));
        Assert.Equal("a\n", sh.Run("true && echo a || echo b"));
    }

    [Fact]
    public void Variables_AssignExportAndUnset()
    {
        using var sh = new ShellHarness();
        sh.Run("X=5");
        Assert.Equal("x=5\n", sh.Run("echo x=$X"));
        sh.Run("unset X");
        Assert.Equal("x=\n", sh.Run("echo x=$X"));
    }

    [Fact]
    public void Cd_ChangesDirectoryAndSupportsDash()
    {
        using var sh = new ShellHarness();
        Directory.CreateDirectory(sh.PathOf("a/b"));

        sh.Run("cd a/b");
        Assert.Equal(sh.PathOf("a/b") + "\n", sh.Run("pwd"));
        sh.Run("cd ..");
        Assert.Equal(sh.PathOf("a") + "\n", sh.Run("pwd"));
        sh.Run("cd -");
        Assert.Equal(sh.PathOf("a/b") + "\n", sh.Run("pwd"));
        Assert.Contains("No such directory", sh.Run("cd missing"));
    }

    [Fact]
    public void Globs_ExpandSortedAndStayLiteralWithoutMatch()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("b.txt"), "");
        File.WriteAllText(sh.PathOf("a.txt"), "");
        File.WriteAllText(sh.PathOf("c.md"), "");

        Assert.Equal("a.txt b.txt\n", sh.Run("echo *.txt"));
        Assert.Equal("*.none\n", sh.Run("echo *.none"));
        Assert.Equal("*.txt\n", sh.Run("echo '*.txt'"));
    }

    [Fact]
    public void UnknownCommand_ReportsNotFound()
    {
        using var sh = new ShellHarness();
        Assert.Equal("sh: nope: command not found\n", sh.Run("nope"));
        Assert.Equal(127, sh.Shell.LastStatus);
    }

    [Fact]
    public void SyntaxError_IsReportedNotThrown()
    {
        using var sh = new ShellHarness();
        Assert.StartsWith("sh: ", sh.Run("echo 'oops"));
        Assert.Equal(2, sh.Shell.LastStatus);
    }

    [Fact]
    public void History_RecordsLines()
    {
        using var sh = new ShellHarness();
        sh.Run("echo a");
        sh.Run("echo b");
        Assert.Equal(new[] { "echo a", "echo b" }, sh.Shell.History);
    }

    [Fact]
    public void Complete_CommandNames()
    {
        using var sh = new ShellHarness();
        Assert.Equal("ory ", sh.Shell.Complete("hist", out _));
    }

    [Fact]
    public void Complete_PathsAndListsAmbiguousCandidates()
    {
        using var sh = new ShellHarness();
        Directory.CreateDirectory(sh.PathOf("documents"));
        File.WriteAllText(sh.PathOf("draft.txt"), "");

        Assert.Equal("cuments/", sh.Shell.Complete("cd do", out _));

        string insert = sh.Shell.Complete("cat d", out var candidates);
        Assert.Equal(string.Empty, insert);
        Assert.Equal(new[] { "documents/", "draft.txt" }, candidates);
    }
}
