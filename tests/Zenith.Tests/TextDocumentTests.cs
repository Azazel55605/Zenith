using System.IO;
using Zenith.Core.Text;

namespace Zenith.Tests;

public class TextDocumentTests
{
    private static TextDocument Typed(string text)
    {
        var doc = new TextDocument();
        doc.Insert(text);
        return doc;
    }

    [Fact]
    public void TypingAndNewLines()
    {
        TextDocument doc = Typed("ab\ncd");
        Assert.Equal("ab\ncd", doc.Text);
        Assert.Equal((1, 2), (doc.Row, doc.Column));
        Assert.True(doc.Modified);
    }

    [Fact]
    public void NewLine_KeepsIndentation()
    {
        TextDocument doc = Typed("    if x");
        doc.NewLine();
        doc.Insert("y");
        Assert.Equal("    if x\n    y", doc.Text);
    }

    [Fact]
    public void Backspace_JoinsLinesAtColumnZero()
    {
        TextDocument doc = Typed("ab\ncd");
        doc.MoveHome();
        doc.MoveHome();   // second Home: column 0
        doc.Backspace();
        Assert.Equal("abcd", doc.Text);
        Assert.Equal((0, 2), (doc.Row, doc.Column));
    }

    [Fact]
    public void Delete_JoinsWithNextLineAtEnd()
    {
        TextDocument doc = Typed("ab\ncd");
        doc.MoveTo(0, 2);
        doc.Delete();
        Assert.Equal("abcd", doc.Text);
    }

    [Fact]
    public void VerticalMovement_RemembersColumn()
    {
        TextDocument doc = Typed("long line\nx\nanother line");
        doc.MoveTo(0, 7);
        doc.MoveVertical(1);
        Assert.Equal((1, 1), (doc.Row, doc.Column));   // clamped on the short line
        doc.MoveVertical(1);
        Assert.Equal((2, 7), (doc.Row, doc.Column));   // back to the remembered column
    }

    [Fact]
    public void LeftRight_WrapAcrossLines()
    {
        TextDocument doc = Typed("a\nb");
        doc.MoveTo(1, 0);
        doc.MoveLeft();
        Assert.Equal((0, 1), (doc.Row, doc.Column));
        doc.MoveRight();
        Assert.Equal((1, 0), (doc.Row, doc.Column));
    }

    [Fact]
    public void SaveAndOpen_RoundTripWithTrailingNewline()
    {
        string path = Path.Combine(Path.GetTempPath(), "zenith-doc-" + System.Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            TextDocument doc = Typed("one\ntwo");
            doc.Save(path);
            Assert.False(doc.Modified);
            Assert.Equal("one\ntwo\n", File.ReadAllText(path));

            TextDocument reopened = TextDocument.Open(path);
            Assert.Equal(path, reopened.Path);
            Assert.False(reopened.Modified);
            reopened.Save();
            Assert.Equal("one\ntwo\n", File.ReadAllText(path));   // no extra newline per save
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_MissingFileIsANewEmptyDocument()
    {
        TextDocument doc = TextDocument.Open("/nonexistent/zenith/file.txt");
        Assert.Equal(string.Empty, doc.Text);
        Assert.False(doc.Modified);
    }

    [Fact]
    public void Save_WithoutPathThrows()
    {
        Assert.Throws<System.InvalidOperationException>(() => new TextDocument().Save());
    }
}
