using System;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Shell;

internal enum TokenKind
{
    Word,
    Pipe,           // |
    RedirectOut,    // >
    RedirectAppend, // >>
    RedirectIn,     // <
    Sequence,       // ;
    And,            // &&
    Or,             // ||
    Newline,        // end of a line; separates commands like ;
}

internal readonly struct Token
{
    public Token(TokenKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public TokenKind Kind { get; }

    /// <summary>The operator, or the word exactly as typed (quotes included) until it is expanded.</summary>
    public string Text { get; }
}

/// <summary>
/// The input ends in the middle of something (an open quote, <c>if</c> without <c>fi</c>, a
/// trailing <c>|</c>). Interactive shells answer with a continuation prompt instead of an error.
/// </summary>
internal sealed class IncompleteInputException : FormatException
{
    public IncompleteInputException(string message) : base(message)
    {
    }
}

/// <summary>What word expansion needs from the shell.</summary>
internal interface IExpansionContext
{
    /// <summary>A variable's value ("" when unset), including the special ones: ?, #, 0-9.</summary>
    string Get(string name);

    /// <summary>The positional parameters $1..$n, for "$@".</summary>
    IReadOnlyList<string> Positional { get; }

    /// <summary>Runs a command substitution and returns its output.</summary>
    string Substitute(string command);
}

/// <summary>
/// POSIX-shell-flavoured lexing and word expansion, in two steps like sh: <see cref="Tokenize"/>
/// splits text into raw words and operators without interpreting them, and
/// <see cref="ExpandFields"/> expands one word right before its command runs (so
/// <c>false; echo $?</c> sees the status of <c>false</c>).
/// <list type="bullet">
/// <item>Single quotes are literal; double quotes allow <c>$</c> expansions; backslash escapes one character.</item>
/// <item><c>$NAME ${NAME} $? $# $@ $0-$9</c>, <c>$(command)</c> and <c>`command`</c>, <c>$((arithmetic))</c>.</item>
/// <item>A leading <c>~</c> is the home directory.</item>
/// <item>Unquoted expansion results are split into fields on whitespace (<c>for f in $(ls)</c>).</item>
/// </list>
/// </summary>
internal static class Parser
{
    /// <summary>Splits text (one line or a whole script) into raw words (quotes kept) and operators.</summary>
    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int wordStart = -1;
        int i = 0;

        void EndWord()
        {
            if (wordStart >= 0)
            {
                tokens.Add(new Token(TokenKind.Word, text.Substring(wordStart, i - wordStart)));
                wordStart = -1;
            }
        }

        while (i < text.Length)
        {
            char c = text[i];

            if (c == ' ' || c == '\t' || c == '\r')
            {
                EndWord();
                i++;
            }
            else if (c == '\n')
            {
                EndWord();
                if (tokens.Count > 0 && tokens[^1].Kind != TokenKind.Newline)
                {
                    tokens.Add(new Token(TokenKind.Newline, "\n"));
                }

                i++;
            }
            else if (c == '#' && wordStart < 0)
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;   // comment to the end of the line
                }
            }
            else if (c == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                EndWord();   // line continuation
                i += 2;
            }
            else if (c == '|' || c == ';' || c == '&' || c == '>' || c == '<')
            {
                EndWord();
                bool doubled = i + 1 < text.Length && text[i + 1] == c;
                TokenKind kind = c switch
                {
                    '|' => doubled ? TokenKind.Or : TokenKind.Pipe,
                    ';' => TokenKind.Sequence,
                    '&' => doubled ? TokenKind.And : throw new FormatException("background jobs (&) are not supported"),
                    '>' => doubled ? TokenKind.RedirectAppend : TokenKind.RedirectOut,
                    _ => TokenKind.RedirectIn,
                };
                bool consumesTwo = doubled && c != ';' && c != '<';
                tokens.Add(new Token(kind, consumesTwo ? text.Substring(i, 2) : c.ToString()));
                i += consumesTwo ? 2 : 1;
            }
            else
            {
                if (wordStart < 0)
                {
                    wordStart = i;
                }

                if (c == '\'' || c == '"' || c == '`')
                {
                    i = SkipQuoted(text, i);
                }
                else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    i = FindClosingParen(text, i + 1) + 1;
                }
                else if (c == '\\')
                {
                    if (i + 1 >= text.Length)
                    {
                        throw new IncompleteInputException("unexpected end of input after \\");
                    }

                    i += 2;
                }
                else
                {
                    i++;
                }
            }
        }

        EndWord();
        return tokens;
    }

    /// <summary>Returns the index just past the quoted section starting at <paramref name="i"/>.</summary>
    private static int SkipQuoted(string text, int i)
    {
        char quote = text[i++];
        while (i < text.Length && text[i] != quote)
        {
            if (quote == '"' && text[i] == '$' && i + 1 < text.Length && text[i + 1] == '(')
            {
                i = FindClosingParen(text, i + 1) + 1;
            }
            else
            {
                i += quote != '\'' && text[i] == '\\' && i + 1 < text.Length ? 2 : 1;
            }
        }

        if (i >= text.Length)
        {
            throw new IncompleteInputException("unterminated quote");
        }

        return i + 1;
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="open"/>, skipping quoted parts and nested parens.</summary>
    private static int FindClosingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' || c == '"' || c == '`')
            {
                i = SkipQuoted(text, i) - 1;
            }
            else if (c == '\\')
            {
                i++;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        throw new IncompleteInputException("unterminated $(");
    }

    /// <summary>
    /// Expands one raw word into zero or more fields: quotes removed, escapes applied, expansions
    /// substituted, unquoted expansion results split on whitespace. <paramref name="globbable"/>
    /// reports an unquoted * or ? for file name matching.
    /// </summary>
    public static List<string> ExpandFields(string raw, IExpansionContext context, out bool globbable)
    {
        var fields = new FieldBuilder();
        globbable = false;
        int i = 0;

        if (raw.Length > 0 && raw[0] == '~' && (raw.Length == 1 || raw[1] == '/'))
        {
            fields.Literal(context.Get("HOME"));
            i = 1;
        }

        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\'')
            {
                int end = raw.IndexOf('\'', i + 1);
                fields.Literal(raw.Substring(i + 1, end - i - 1));
                i = end + 1;
            }
            else if (c == '"')
            {
                fields.Literal(string.Empty);   // "" is an empty field, not nothing
                i++;
                while (raw[i] != '"')
                {
                    if (raw[i] == '\\' && (raw[i + 1] == '"' || raw[i + 1] == '\\' || raw[i + 1] == '$' || raw[i + 1] == '`'))
                    {
                        fields.Literal(raw[i + 1].ToString());
                        i += 2;
                    }
                    else if (raw[i] == '$' || raw[i] == '`')
                    {
                        i = Expand(raw, i, context, fields, quoted: true);
                    }
                    else
                    {
                        fields.Literal(raw[i++].ToString());
                    }
                }

                i++;
            }
            else if (c == '\\' && i + 1 < raw.Length)
            {
                fields.Literal(raw[i + 1].ToString());
                i += 2;
            }
            else if (c == '$' || c == '`')
            {
                i = Expand(raw, i, context, fields, quoted: false);
            }
            else
            {
                globbable |= c == '*' || c == '?';
                fields.Literal(c.ToString());
                i++;
            }
        }

        return fields.Finish();
    }

    /// <summary>Expands a word to a single string (fields joined by spaces): for assignments, redirection targets and tests.</summary>
    public static string ExpandWord(string raw, IExpansionContext context, out bool globbable)
        => string.Join(" ", ExpandFields(raw, context, out globbable));

    /// <summary>Variable-only expansion (no command substitution), as used by the unit tests.</summary>
    public static string ExpandWord(string raw, Func<string, string> lookupVariable, out bool globbable)
        => ExpandWord(raw, new LookupContext(lookupVariable), out globbable);

    /// <summary>Expands the <c>$...</c> or <c>`...`</c> starting at <paramref name="i"/>; returns the index after it.</summary>
    private static int Expand(string raw, int i, IExpansionContext context, FieldBuilder fields, bool quoted)
    {
        void Emit(string value)
        {
            if (quoted)
            {
                fields.Literal(value);
            }
            else
            {
                fields.Split(value);
            }
        }

        if (raw[i] == '`')
        {
            int end = raw.IndexOf('`', i + 1);
            Emit(TrimTrailingNewlines(context.Substitute(raw.Substring(i + 1, end - i - 1))));
            return end + 1;
        }

        int start = i + 1;
        if (start >= raw.Length)
        {
            fields.Literal("$");
            return start;
        }

        char next = raw[start];
        if (next == '(')
        {
            int close = FindClosingParen(raw, start);
            bool arithmetic = start + 1 < raw.Length && raw[start + 1] == '(' && raw[close - 1] == ')'
                && FindClosingParen(raw, start + 1) == close - 1;
            if (arithmetic)
            {
                // POSIX: the expression first gets parameter expansion and command substitution.
                string expression = ExpandInline(raw.Substring(start + 2, close - start - 3), context);
                Emit(Arithmetic.Evaluate(expression, context.Get).ToString());
            }
            else
            {
                Emit(TrimTrailingNewlines(context.Substitute(raw.Substring(start + 1, close - start - 1))));
            }

            return close + 1;
        }

        if (next == '@' || next == '*')
        {
            IReadOnlyList<string> args = context.Positional;
            if (quoted && next == '@')
            {
                // "$@": one field per argument.
                for (int a = 0; a < args.Count; a++)
                {
                    if (a > 0)
                    {
                        fields.Break();
                    }

                    fields.Literal(args[a]);
                }
            }
            else
            {
                Emit(string.Join(" ", args));
            }

            return start + 1;
        }

        if (next == '?' || next == '#' || char.IsDigit(next))
        {
            Emit(context.Get(next.ToString()));
            return start + 1;
        }

        if (next == '{')
        {
            int close = raw.IndexOf('}', start);
            if (close < 0)
            {
                throw new FormatException("bad substitution");
            }

            Emit(context.Get(raw.Substring(start + 1, close - start - 1)));
            return close + 1;
        }

        int end2 = start;
        while (end2 < raw.Length && (char.IsLetterOrDigit(raw[end2]) || raw[end2] == '_'))
        {
            end2++;
        }

        if (end2 == start)
        {
            fields.Literal("$");   // a lone $ is literal
            return start;
        }

        Emit(context.Get(raw.Substring(start, end2 - start)));
        return end2;
    }

    /// <summary>Expands $ and ` sequences in <paramref name="text"/> as if double-quoted, leaving everything else as is.</summary>
    private static string ExpandInline(string text, IExpansionContext context)
    {
        var fields = new FieldBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '$' || text[i] == '`')
            {
                i = Expand(text, i, context, fields, quoted: true);
            }
            else
            {
                fields.Literal(text[i++].ToString());
            }
        }

        return string.Join(" ", fields.Finish());
    }

    private static string TrimTrailingNewlines(string text) => text.TrimEnd('\n', '\r');

    /// <summary>Shell wildcard match: * any run of characters, ? any single character. Case-insensitive, as the FAT volumes are.</summary>
    public static bool GlobMatch(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>Accumulates fields; <see cref="Split"/> breaks on whitespace, <see cref="Literal"/> never does.</summary>
    private sealed class FieldBuilder
    {
        private readonly List<string> _fields = new();
        private readonly StringBuilder _current = new();
        private bool _started;

        public void Literal(string text)
        {
            _current.Append(text);
            _started = true;
        }

        public void Split(string text)
        {
            foreach (char c in text)
            {
                if (c == ' ' || c == '\t' || c == '\n')
                {
                    Break();
                }
                else
                {
                    _current.Append(c);
                    _started = true;
                }
            }
        }

        public void Break()
        {
            if (_started)
            {
                _fields.Add(_current.ToString());
                _current.Clear();
                _started = false;
            }
        }

        public List<string> Finish()
        {
            Break();
            return _fields;
        }
    }

    private sealed class LookupContext : IExpansionContext
    {
        private readonly Func<string, string> _lookup;

        public LookupContext(Func<string, string> lookup) => _lookup = lookup;

        public IReadOnlyList<string> Positional => Array.Empty<string>();

        public string Get(string name) => _lookup(name);

        public string Substitute(string command) => throw new FormatException("command substitution is not available here");
    }
}
