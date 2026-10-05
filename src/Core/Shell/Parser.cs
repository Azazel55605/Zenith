using System;
using System.Collections.Generic;
using System.Text;

namespace MyOS.Core.Shell;

internal enum TokenKind
{
    Word,
    Pipe,          // |
    RedirectOut,   // >
    RedirectAppend, // >>
    RedirectIn,    // <
    Sequence,      // ;
    And,           // &&
    Or,            // ||
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
/// POSIX-shell-flavoured parsing in two steps, like sh: <see cref="Tokenize"/> splits a line into
/// words and operators without interpreting them, and <see cref="ExpandWord"/> expands one word
/// right before its command runs (so <c>false; echo $?</c> sees the status of <c>false</c>).
/// Single quotes are literal, double quotes allow <c>$VAR</c>, backslash escapes one character,
/// and <c>~</c> at the start of a word is the home directory.
/// </summary>
internal static class Parser
{
    /// <summary>Splits a line into raw words (quotes kept) and operators.</summary>
    public static List<Token> Tokenize(string line)
    {
        var tokens = new List<Token>();
        int wordStart = -1;
        int i = 0;

        void EndWord()
        {
            if (wordStart >= 0)
            {
                tokens.Add(new Token(TokenKind.Word, line.Substring(wordStart, i - wordStart)));
                wordStart = -1;
            }
        }

        while (i < line.Length)
        {
            char c = line[i];

            if (c == ' ' || c == '\t')
            {
                EndWord();
                i++;
            }
            else if (c == '#' && wordStart < 0)
            {
                break;   // comment
            }
            else if (c == '|' || c == ';' || c == '&' || c == '>' || c == '<')
            {
                EndWord();
                bool doubled = i + 1 < line.Length && line[i + 1] == c;
                TokenKind kind = c switch
                {
                    '|' => doubled ? TokenKind.Or : TokenKind.Pipe,
                    ';' => TokenKind.Sequence,
                    '&' => doubled ? TokenKind.And : throw new FormatException("background jobs (&) are not supported"),
                    '>' => doubled ? TokenKind.RedirectAppend : TokenKind.RedirectOut,
                    _ => TokenKind.RedirectIn,
                };
                bool consumesTwo = doubled && c != ';' && c != '<';
                tokens.Add(new Token(kind, consumesTwo ? line.Substring(i, 2) : c.ToString()));
                i += consumesTwo ? 2 : 1;
            }
            else
            {
                if (wordStart < 0)
                {
                    wordStart = i;
                }

                if (c == '\'' || c == '"')
                {
                    i = SkipQuoted(line, i);
                }
                else
                {
                    i += c == '\\' && i + 1 < line.Length ? 2 : 1;
                }
            }
        }

        EndWord();
        return tokens;
    }

    /// <summary>Returns the index just past the quoted section starting at <paramref name="i"/>.</summary>
    private static int SkipQuoted(string line, int i)
    {
        char quote = line[i++];
        while (i < line.Length && line[i] != quote)
        {
            i += quote == '"' && line[i] == '\\' && i + 1 < line.Length ? 2 : 1;
        }

        if (i >= line.Length)
        {
            throw new FormatException("unterminated quote");
        }

        return i + 1;
    }

    /// <summary>
    /// Expands one raw word: quotes removed, escapes applied, variables and a leading ~ substituted.
    /// <paramref name="globbable"/> reports an unquoted * or ? for file name matching.
    /// </summary>
    public static string ExpandWord(string raw, Func<string, string> lookupVariable, out bool globbable)
    {
        var word = new StringBuilder();
        globbable = false;
        int i = 0;

        if (raw.Length > 0 && raw[0] == '~' && (raw.Length == 1 || raw[1] == '/'))
        {
            word.Append(lookupVariable("HOME"));
            i = 1;
        }

        while (i < raw.Length)
        {
            char c = raw[i];
            if (c == '\'')
            {
                int end = raw.IndexOf('\'', i + 1);
                word.Append(raw, i + 1, end - i - 1);
                i = end + 1;
            }
            else if (c == '"')
            {
                i++;
                while (raw[i] != '"')
                {
                    if (raw[i] == '\\' && (raw[i + 1] == '"' || raw[i + 1] == '\\' || raw[i + 1] == '$'))
                    {
                        word.Append(raw[i + 1]);
                        i += 2;
                    }
                    else if (raw[i] == '$')
                    {
                        i = ExpandVariable(raw, i, word, lookupVariable);
                    }
                    else
                    {
                        word.Append(raw[i++]);
                    }
                }

                i++;
            }
            else if (c == '\\' && i + 1 < raw.Length)
            {
                word.Append(raw[i + 1]);
                i += 2;
            }
            else if (c == '$')
            {
                i = ExpandVariable(raw, i, word, lookupVariable);
            }
            else
            {
                globbable |= c == '*' || c == '?';
                word.Append(c);
                i++;
            }
        }

        return word.ToString();
    }

    /// <summary>Expands <c>$NAME</c>, <c>${NAME}</c> or <c>$?</c> starting at <paramref name="i"/>; returns the index after it.</summary>
    private static int ExpandVariable(string line, int i, StringBuilder word, Func<string, string> lookupVariable)
    {
        int start = i + 1;
        if (start < line.Length && line[start] == '?')
        {
            word.Append(lookupVariable("?"));
            return start + 1;
        }

        if (start < line.Length && line[start] == '{')
        {
            int close = line.IndexOf('}', start);
            if (close < 0)
            {
                throw new FormatException("bad substitution");
            }

            word.Append(lookupVariable(line.Substring(start + 1, close - start - 1)));
            return close + 1;
        }

        int end = start;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
        {
            end++;
        }

        if (end == start)
        {
            word.Append('$');   // a lone $ is literal
            return start;
        }

        word.Append(lookupVariable(line.Substring(start, end - start)));
        return end;
    }

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
}
