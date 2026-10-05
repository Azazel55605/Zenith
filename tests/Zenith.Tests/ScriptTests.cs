using System.IO;
using Zenith.Core.Shell;

namespace Zenith.Tests;

public class ScriptTests
{
    [Theory]
    [InlineData("if true; then echo yes; else echo no; fi", "yes\n")]
    [InlineData("if false; then echo yes; elif true; then echo elif; else echo no; fi", "elif\n")]
    [InlineData("if false; then echo yes; fi; echo after", "after\n")]
    [InlineData("for x in a b c; do echo $x; done", "a\nb\nc\n")]
    [InlineData("i=0; while [ $i -lt 3 ]; do echo $i; i=$((i+1)); done", "0\n1\n2\n")]
    [InlineData("i=0; until [ $i -eq 2 ]; do i=$((i+1)); done; echo $i", "2\n")]
    [InlineData("for x in 1 2 3 4; do if [ $x = 3 ]; then break; fi; echo $x; done", "1\n2\n")]
    [InlineData("for x in 1 2 3; do if [ $x = 2 ]; then continue; fi; echo $x; done", "1\n3\n")]
    [InlineData("for a in 1 2; do for b in x y; do if [ $b = y ]; then continue 2; fi; echo $a$b; done; done", "1x\n2x\n")]
    [InlineData("{ echo a; echo b; } | wc -l", "2\n")]
    [InlineData("! false && echo negated", "negated\n")]
    public void ControlFlow(string script, string expected)
    {
        using var sh = new ShellHarness();
        Assert.Equal(expected, sh.Run(script));
    }

    [Theory]
    [InlineData("echo $((1 + 2 * 3))", "7\n")]
    [InlineData("echo $(( (1 + 2) * 3 ))", "9\n")]
    [InlineData("echo $((10 / 3)) $((10 % 3)) $((-4))", "3 1 -4\n")]
    [InlineData("x=5; echo $((x * 2)) $(($x + 1))", "10 6\n")]
    [InlineData("echo $((3 > 2)) $((3 == 2)) $((1 && 0)) $((!0))", "1 0 0 1\n")]
    [InlineData("echo $((2**10)) $((2**3**2)) $((-2**2)) $((3*2**2))", "1024 512 4 12\n")]
    public void Arithmetic(string script, string expected)
    {
        using var sh = new ShellHarness();
        Assert.Equal(expected, sh.Run(script));
    }

    [Fact]
    public void Arithmetic_DivisionByZeroIsAnError()
    {
        using var sh = new ShellHarness();
        Assert.Contains("division by zero", sh.Run("echo $((1/0))"));
    }

    [Fact]
    public void CommandSubstitution_AndFieldSplitting()
    {
        using var sh = new ShellHarness();
        sh.Run("touch b.txt a.txt");
        Assert.Equal("a.txt\nb.txt\n", sh.Run("for f in $(ls); do echo $f; done"));
        Assert.Equal("[a.txt\nb.txt]\n", sh.Run("echo \"[$(ls)]\""));   // quoted: the newline survives
        Assert.Equal("one-two\n", sh.Run("x=$(echo one); echo $x-`echo two`"));
        Assert.Equal("3\n", sh.Run("echo $(echo $(( $(echo 1) + 2 )))"));
    }

    [Fact]
    public void QuotedExpansions_AreNotSplit()
    {
        using var sh = new ShellHarness();
        sh.Run("X='a  b'");
        Assert.Equal("2\n", sh.Run("for w in $X; do echo $w; done | wc -l").Trim() + "\n");
        Assert.Equal("1\n", sh.Run("for w in \"$X\"; do echo \"$w\"; done | wc -l").Trim() + "\n");
    }

    [Fact]
    public void Functions_PositionalParametersAndReturn()
    {
        using var sh = new ShellHarness();
        sh.Run("greet() { echo \"hello $1 ($#)\"; return 3; }");
        Assert.Equal("hello world (2)\n", sh.Run("greet world extra"));
        Assert.Equal(3, sh.Shell.LastStatus);

        sh.Run("function count { echo $#; }");
        Assert.Equal("0\n", sh.Run("count"));
        Assert.Contains("shell function", sh.Run("type greet"));
    }

    [Fact]
    public void Functions_Recursion()
    {
        using var sh = new ShellHarness();
        sh.Run("fact() { if [ $1 -le 1 ]; then echo 1; else echo $(( $1 * $(fact $(( $1 - 1 ))) )); fi; }");
        Assert.Equal("120\n", sh.Run("fact 5"));
    }

    [Fact]
    public void Scripts_RunWithShInAChildShell()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("s.sh"),
            "#!/bin/sh\n# a comment\nINNER=set\necho \"args: $@\"\nfor a in \"$@\"; do\n  echo \"[$a]\"\ndone\nexit 4\necho unreachable\n");

        Assert.Equal("args: x y z\n[x]\n[y z]\n", sh.Run("sh s.sh x 'y z'"));
        Assert.Equal(4, sh.Shell.LastStatus);
        Assert.Equal("inner=\n", sh.Run("echo inner=$INNER"));   // child variables do not leak
    }

    [Fact]
    public void Scripts_SourceRunsInTheCurrentShell()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("vars.sh"), "FROM_SOURCE=yes\n");
        sh.Run(". ./vars.sh");
        Assert.Equal("yes\n", sh.Run("echo $FROM_SOURCE"));
    }

    [Fact]
    public void Scripts_AreFoundOnPathAndByRelativePath()
    {
        using var sh = new ShellHarness();
        Directory.CreateDirectory(sh.PathOf("bin"));
        File.WriteAllText(sh.PathOf("bin/hello"), "echo \"hi $1\"\n");
        sh.Run("PATH=" + sh.PathOf("bin"));

        Assert.Equal("hi there\n", sh.Run("hello there"));
        Assert.Equal("hi you\n", sh.Run("./bin/hello you"));
        Assert.Contains("is " + sh.PathOf("bin/hello"), sh.Run("type hello"));
    }

    [Fact]
    public void ShDashC_AndPipedScripts()
    {
        using var sh = new ShellHarness();
        Assert.Equal("a-b\n", sh.Run("sh -c 'echo $1-$2' a b"));
        Assert.Equal("piped\n", sh.Run("echo 'echo piped' | sh"));
    }

    [Fact]
    public void Read_FromRedirectedFileLineByLine()
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("lines.txt"), "alpha one\nbeta two\n");
        Assert.Equal("alpha:one\nbeta:two\n", sh.Run("while read name rest; do echo $name:$rest; done < lines.txt"));
        Assert.Equal("x\n", sh.Run("echo x | read v; echo $v"));
    }

    [Fact]
    public void Read_InteractiveUsesTheHostCallback()
    {
        using var sh = new ShellHarness();
        string? seenPrompt = null;
        sh.Shell.ReadInputLine = prompt => { seenPrompt = prompt; return "typed"; };
        Assert.Equal("got typed\n", sh.Run("read -p 'name? ' v; echo got $v"));
        Assert.Equal("name? ", seenPrompt);
    }

    [Theory]
    [InlineData("[ -f f.txt ]", 0)]
    [InlineData("[ -d f.txt ]", 1)]
    [InlineData("[ -d dir ]", 0)]
    [InlineData("[ -e missing ]", 1)]
    [InlineData("[ ! -e missing ]", 0)]
    [InlineData("[ -z '' ]", 0)]
    [InlineData("[ -n '' ]", 1)]
    [InlineData("[ abc = abc ]", 0)]
    [InlineData("[ abc != abc ]", 1)]
    [InlineData("[ 2 -gt 10 ]", 1)]
    [InlineData("[ 2 -lt 10 -a 3 -eq 3 ]", 0)]
    [InlineData("[ 1 -eq 2 -o x = x ]", 0)]
    [InlineData("test ( 1 -eq 1 ) -a ! ( 2 -eq 3 )", 0)]
    [InlineData("[ x -eq 1 ]", 2)]
    [InlineData("[ 1 -eq 1", 2)]
    public void Test(string expression, int status)
    {
        using var sh = new ShellHarness();
        File.WriteAllText(sh.PathOf("f.txt"), "content");
        Directory.CreateDirectory(sh.PathOf("dir"));
        sh.Run(expression.Replace("(", "'('").Replace(")", "')'"));
        Assert.Equal(status, sh.Shell.LastStatus);
    }

    [Fact]
    public void Shift_AndPositionalCount()
    {
        using var sh = new ShellHarness();
        sh.Run("f() { shift; echo $# $1; }");
        Assert.Equal("2 b\n", sh.Run("f a b c"));
    }

    [Fact]
    public void RedirectionOnCompoundCommands()
    {
        using var sh = new ShellHarness();
        sh.Run("for i in 1 2; do echo $i; done > out.txt");
        Assert.Equal("1\n2\n", File.ReadAllText(sh.PathOf("out.txt")));
    }

    [Theory]
    [InlineData("if true; then", false)]
    [InlineData("for x in a b; do echo $x", false)]
    [InlineData("echo 'open", false)]
    [InlineData("echo a |", false)]
    [InlineData("f() {", false)]
    [InlineData("echo done", true)]
    [InlineData("if true; then echo y; fi", true)]
    [InlineData("fi", true)]   // a syntax error is complete: run it to report the error
    public void IsComplete(string text, bool complete)
    {
        Assert.Equal(complete, Shell.IsComplete(text));
    }

    [Fact]
    public void MultiLineInput()
    {
        using var sh = new ShellHarness();
        Assert.Equal("a\nb\n", sh.Run("for x in a b\ndo\n  echo $x\ndone"));
    }

    [Fact]
    public void ExitAtThePromptAsksTheHostToClose()
    {
        using var sh = new ShellHarness();
        bool exited = false;
        sh.Shell.ExitRequested += () => exited = true;
        sh.Run("exit 5");
        Assert.True(exited);
        Assert.Equal(5, sh.Shell.LastStatus);
    }

    [Fact]
    public void Cancel_StopsLoops()
    {
        using var sh = new ShellHarness();
        var run = System.Threading.Tasks.Task.Run(() => sh.Run("while true; do sleep 0.05; done; echo not-reached"));
        System.Threading.Thread.Sleep(200);
        sh.Shell.Cancel();
        Assert.True(run.Wait(System.TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain("not-reached", run.Result);
        Assert.Equal(130, sh.Shell.LastStatus);
    }
}
