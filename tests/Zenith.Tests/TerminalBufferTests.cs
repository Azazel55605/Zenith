using System.Linq;
using System.Text;
using Zenith.Gui.Graphics;
using Zenith.Gui.Terminal;

namespace Zenith.Tests;

public class TerminalBufferTests
{
    private static string LineText(TerminalBuffer buffer, int index)
        => new string(buffer.Line(index).Select(c => c.Char).ToArray());

    [Fact]
    public void Write_SplitsLinesAndKeepsCurrentLineOpen()
    {
        var buffer = new TerminalBuffer(80);
        buffer.Write("one\ntwo");

        Assert.Equal(2, buffer.LineCount);
        Assert.Equal("one", LineText(buffer, 0));
        Assert.Equal(3, buffer.CursorColumn);
    }

    [Fact]
    public void Write_WrapsAtColumnCount()
    {
        var buffer = new TerminalBuffer(4);
        buffer.Write("abcdef");
        Assert.Equal("abcd", LineText(buffer, 0));
        Assert.Equal("ef", LineText(buffer, 1));
    }

    [Fact]
    public void Write_ExpandsTabsToMultiplesOfEight()
    {
        var buffer = new TerminalBuffer(80);
        buffer.Write("ab\tc");
        Assert.Equal("ab      c", LineText(buffer, 0));
    }

    [Fact]
    public void Sgr_ColorsCellsAndResets()
    {
        var buffer = new TerminalBuffer(80);
        buffer.Write("\u001b[31mR\u001b[0mN");

        var cells = buffer.Line(0);
        Assert.Equal("RN", LineText(buffer, 0));
        Assert.NotEqual(Theme.TextPrimary, cells[0].Color);
        Assert.Equal(Theme.TextPrimary, cells[1].Color);
    }

    [Fact]
    public void ClearScreen_EmptiesBuffer()
    {
        var buffer = new TerminalBuffer(80);
        buffer.Write("x\ny\n\u001b[2J\u001b[Hz");
        Assert.Equal(1, buffer.LineCount);
        Assert.Equal("z", LineText(buffer, 0));
    }

    [Fact]
    public void Scrollback_IsCapped()
    {
        var buffer = new TerminalBuffer(80);
        var text = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            text.Append(i).Append('\n');
        }

        buffer.Write(text.ToString());
        Assert.Equal(2000, buffer.LineCount);
    }
}
