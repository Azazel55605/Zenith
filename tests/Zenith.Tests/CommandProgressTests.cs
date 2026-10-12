using System.Linq;
using Zenith.Core.Shell;
using Zenith.Gui.Terminal;

namespace Zenith.Tests;
public class CommandProgressTests
{
    [Fact]
    public void InteractiveUpdatesReuseOneLineAndFinishBeforeNextMessage()
    {
        var output = new BufferOutput(); var progress = new CommandProgress(output, true);
        progress.Report("fetch progress 1/100"); progress.Report("fetch progress 100/100"); progress.Finish(); progress.Finish();
        output.Write("complete\n"); var terminal = new TerminalBuffer(); terminal.Write(output.ToString());
        Assert.Equal(3, terminal.LineCount);
        Assert.Contains("100%", new string(terminal.Line(0).Select(c => c.Char).ToArray()));
        Assert.Equal("complete", new string(terminal.Line(1).Select(c => c.Char).ToArray()));
    }
    [Fact]
    public void RedirectionDoesNotReceiveProgressOrControlCharacters()
    {
        var output = new BufferOutput(); var progress = new CommandProgress(output, false);
        progress.Report("fetch progress 100/100"); progress.Report("Applying files 1/2"); progress.Finish();
        Assert.Equal("", output.ToString());
    }
}
