using System.Collections.Generic;
using System.Linq;
using Zenith.Core.Shell;

namespace Zenith.Tests;

public class ParserTests
{
    private static string Lookup(string name) => name switch
    {
        "HOME" => "/home/user",
        "USER" => "user",
        "?" => "3",
        _ => string.Empty,
    };

    private static string Expand(string raw) => Parser.ExpandWord(raw, Lookup, out _);

    [Fact]
    public void Tokenize_SplitsWordsAndOperators()
    {
        List<Token> tokens = Parser.Tokenize("a | b > f >> g < h; c && d || e");

        Assert.Equal(
            new[] { "a", "|", "b", ">", "f", ">>", "g", "<", "h", ";", "c", "&&", "d", "||", "e" },
            tokens.Select(t => t.Text));
        Assert.Equal(TokenKind.RedirectAppend, tokens[5].Kind);
        Assert.Equal(TokenKind.And, tokens[11].Kind);
        Assert.Equal(TokenKind.Or, tokens[13].Kind);
    }

    [Fact]
    public void Tokenize_KeepsQuotedOperatorsAndSpacesInsideOneWord()
    {
        List<Token> tokens = Parser.Tokenize("echo 'a | b' \"c ; d\" e\\ f");

        Assert.Equal(new[] { "echo", "'a | b'", "\"c ; d\"", "e\\ f" }, tokens.Select(t => t.Text));
    }

    [Fact]
    public void Tokenize_StopsAtComment()
    {
        Assert.Single(Parser.Tokenize("ls # list files"));
    }

    [Theory]
    [InlineData("'unterminated")]
    [InlineData("\"unterminated")]
    [InlineData("a & b")]
    public void Tokenize_RejectsBadSyntax(string line)
    {
        Assert.ThrowsAny<System.FormatException>(() => Parser.Tokenize(line));
    }

    [Theory]
    [InlineData("'$HOME'", "$HOME")]
    [InlineData("\"$HOME\"", "/home/user")]
    [InlineData("${USER}x", "userx")]
    [InlineData("$?", "3")]
    [InlineData("~", "/home/user")]
    [InlineData("~/docs", "/home/user/docs")]
    [InlineData("a~", "a~")]
    [InlineData("\"a \\\"b\\\"\"", "a \"b\"")]
    [InlineData("c\\ d", "c d")]
    [InlineData("$", "$")]
    [InlineData("$UNSET", "")]
    public void ExpandWord(string raw, string expected)
    {
        Assert.Equal(expected, Expand(raw));
    }

    [Theory]
    [InlineData("*.txt", true)]
    [InlineData("'*.txt'", false)]
    [InlineData("\"a?\"", false)]
    [InlineData("plain", false)]
    public void ExpandWord_ReportsUnquotedWildcards(string raw, bool globbable)
    {
        Parser.ExpandWord(raw, Lookup, out bool actual);
        Assert.Equal(globbable, actual);
    }

    [Theory]
    [InlineData("*.txt", "notes.txt", true)]
    [InlineData("*.txt", "notes.md", false)]
    [InlineData("?.c", "a.c", true)]
    [InlineData("?.c", "ab.c", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("*.TXT", "notes.txt", true)]
    [InlineData("*", "", true)]
    public void GlobMatch(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, Parser.GlobMatch(pattern, name));
    }
}
