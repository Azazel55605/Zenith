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
    public void AlternateFramesPreserveHistoryAndKeepTheCaretSeparateFromText()
    {
        var terminal = new TerminalBuffer();
        terminal.Write("history\r\n$ quill\r\n\u001b[?10");
        terminal.Write("49h\u001b[2J\u001b[HQuill\r\ntext\r\n\u001b[7mSave  \u001b[0m\u001b[2;");
        terminal.Write("2H");
        var screen = Assert.IsType<TerminalBuffer>(terminal.Alternate);
        Assert.Equal("text", LineText(screen, 1));
        Assert.Equal(1, screen.CaretRow);
        Assert.Equal(1, screen.CaretColumn);
        Assert.True(screen.Line(2)[5].Inverse);
        terminal.Write("\u001b[2;1H\u001b[2Knew\u001b[2;4H");
        Assert.Equal("new", LineText(screen, 1));
        Assert.Equal("Save  ", LineText(screen, 2));
        Assert.Equal(3, screen.CaretColumn);
        terminal.Write("\u001b[?1049l");
        Assert.Null(terminal.Alternate);
        Assert.Equal("history", LineText(terminal, 0));
        Assert.Equal("$ quill", LineText(terminal, 1));
    }

    [Fact]
    public void CarriageReturnOverwritesAndEraseRemovesPreviousLongStatus()
    {
        var buffer = new TerminalBuffer(); buffer.Write("long progress status");
        buffer.Write("\rshort"); Assert.Equal("shortprogress status", LineText(buffer, 0));
        buffer.Write("\r\u001b[2Kdone\nnext\r\nlast");
        Assert.Equal("done", LineText(buffer, 0)); Assert.Equal("next", LineText(buffer, 1));
        Assert.Equal("last", LineText(buffer, 2)); Assert.Equal(4, buffer.CursorColumn);
    }

    [Fact]
    public void Write_SplitsLinesAndKeepsCurrentLineOpen()
    {
        var buffer = new TerminalBuffer();
        buffer.Write("one\ntwo");

        Assert.Equal(2, buffer.LineCount);
        Assert.Equal("one", LineText(buffer, 0));
        Assert.Equal(3, buffer.CursorColumn);
    }

    [Fact]
    public void Lines_StayLogicalAndWrapAtAnyWidth()
    {
        var buffer = new TerminalBuffer();
        buffer.Write("abcdef");
        Assert.Equal(1, buffer.LineCount);

        var rows = TerminalBuffer.Wrap(buffer.Line(0), 4);
        Assert.Equal(new[] { "abcd", "ef" }, rows.Select(r => new string(r.Select(c => c.Char).ToArray())));
        Assert.Equal(2, TerminalBuffer.RowCount(buffer.Line(0), 4));
        Assert.Equal(1, TerminalBuffer.RowCount(buffer.Line(0), 6));
        Assert.Single(TerminalBuffer.Wrap(new System.Collections.Generic.List<Cell>(), 4));
    }

    [Fact]
    public void Write_ExpandsTabsToMultiplesOfEight()
    {
        var buffer = new TerminalBuffer();
        buffer.Write("ab\tc");
        Assert.Equal("ab      c", LineText(buffer, 0));
    }

    [Fact]
    public void Sgr_ColorsCellsAndResets()
    {
        var buffer = new TerminalBuffer();
        buffer.Write("\u001b[31mR\u001b[0mN");

        var cells = buffer.Line(0);
        Assert.Equal("RN", LineText(buffer, 0));
        Assert.NotEqual(Theme.TextPrimary, cells[0].Color);
        Assert.Equal(Theme.TextPrimary, cells[1].Color);
    }

    [Fact]
    public void ClearScreen_EmptiesBuffer()
    {
        var buffer = new TerminalBuffer();
        buffer.Write("x\ny\n\u001b[2J\u001b[Hz");
        Assert.Equal(1, buffer.LineCount);
        Assert.Equal("z", LineText(buffer, 0));
    }

    [Fact]
    public void Scrollback_IsCapped()
    {
        var buffer = new TerminalBuffer();
        var text = new StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            text.Append(i).Append('\n');
        }

        buffer.Write(text.ToString());
        Assert.Equal(2000, buffer.LineCount);
    }
    [Fact]
    public void ExistingScrollbackRetainsAnsiIntentAcrossAppearanceChanges()
    {
        var buffer = new TerminalBuffer();buffer.Write("N\u001b[32mG\u001b[1mB\u001b[0mR");
        var cells = buffer.Line(0);
        Assert.Equal(0xff202126U, cells[0].ResolveColor(0xff202126, true));
        Assert.Equal(0xfff1f2f4U, cells[0].ResolveColor(0xfff1f2f4, false));
        Assert.Equal(TerminalColors.Resolve(2, true), cells[1].ResolveColor(0xff202126, true));
        Assert.Equal(TerminalColors.Resolve(10, false), cells[2].ResolveColor(0xfff1f2f4, false));
        Assert.Equal(0xff202126U, cells[3].ResolveColor(0xff202126, true));
        var literal = new Cell('x', 0xff123456);Assert.Equal(0xff123456U, literal.ResolveColor(0xffffffff, true));
        var copy = new TerminalBuffer();copy.AppendCells(cells);
        Assert.Equal(cells[1].ResolveColor(0xff202126, true), copy.Line(0)[1].ResolveColor(0xff202126, true));
    }
}
