using System;
using System.Collections.Generic;

namespace Zenith.Core.Shell;

/// <summary>A redirection attached to a command: <c>&lt; file</c>, <c>&gt; file</c> or <c>&gt;&gt; file</c>.</summary>
internal readonly struct Redirect
{
    public Redirect(TokenKind kind, string target)
    {
        Kind = kind;
        Target = target;
    }

    public TokenKind Kind { get; }

    /// <summary>The target as typed (expanded when the command runs).</summary>
    public string Target { get; }
}

/// <summary>A node of the shell syntax tree. Any command, simple or compound, can carry redirections.</summary>
internal abstract class Node
{
    public List<Redirect> Redirects { get; } = new();
}

/// <summary><c>name args...</c>, or only assignments (<c>X=1 Y=2</c>).</summary>
internal sealed class SimpleCommandNode : Node
{
    public List<string> Words { get; } = new();
}

/// <summary><c>a | b | c</c>, optionally negated with <c>!</c>.</summary>
internal sealed class PipelineNode : Node
{
    public List<Node> Stages { get; } = new();
    public bool Negate { get; set; }
}

/// <summary><c>a &amp;&amp; b || c</c>: <see cref="Operators"/>[i] joins Items[i] and Items[i + 1].</summary>
internal sealed class AndOrNode : Node
{
    public List<Node> Items { get; } = new();
    public List<TokenKind> Operators { get; } = new();
}

/// <summary>Commands separated by <c>;</c> or newlines.</summary>
internal sealed class ListNode : Node
{
    public List<Node> Items { get; } = new();
}

internal sealed class IfNode : Node
{
    public List<(ListNode Condition, ListNode Body)> Branches { get; } = new();
    public ListNode? Else { get; set; }
}

/// <summary><c>for name [in words]; do ...; done</c>. Without <c>in</c>, loops over "$@".</summary>
internal sealed class ForNode : Node
{
    public ForNode(string variable, List<string>? words, ListNode body)
    {
        Variable = variable;
        Words = words;
        Body = body;
    }

    public string Variable { get; }
    public List<string>? Words { get; }
    public ListNode Body { get; }
}

/// <summary><c>while list; do ...; done</c>, or <c>until</c> when <see cref="Until"/>.</summary>
internal sealed class WhileNode : Node
{
    public WhileNode(ListNode condition, ListNode body, bool until)
    {
        Condition = condition;
        Body = body;
        Until = until;
    }

    public ListNode Condition { get; }
    public ListNode Body { get; }
    public bool Until { get; }
}

/// <summary><c>{ list; }</c>.</summary>
internal sealed class GroupNode : Node
{
    public GroupNode(ListNode body) => Body = body;

    public ListNode Body { get; }
}

/// <summary><c>name() { ...; }</c> or <c>function name { ...; }</c>: defines, does not run.</summary>
internal sealed class FunctionNode : Node
{
    public FunctionNode(string name, Node body)
    {
        Name = name;
        Body = body;
    }

    public string Name { get; }
    public Node Body { get; }
}

/// <summary>
/// Recursive-descent parser for the POSIX shell grammar subset Zenith supports:
/// <code>
/// list     := and_or ((';' | newline) and_or)*
/// and_or   := pipeline (('&amp;&amp;' | '||') newline* pipeline)*
/// pipeline := ['!'] command ('|' newline* command)*
/// command  := if | for | while | until | '{' list '}' | function | simple
/// </code>
/// Reserved words are only recognized where a command starts, so <c>echo done</c> is fine.
/// Running out of input inside a construct throws <see cref="IncompleteInputException"/>.
/// </summary>
internal sealed class ScriptParser
{
    private static readonly HashSet<string> s_reserved = new()
    {
        "then", "elif", "else", "fi", "do", "done", "}", "in",
    };

    private readonly List<Token> _tokens;
    private int _pos;

    private ScriptParser(List<Token> tokens) => _tokens = tokens;

    public static ListNode Parse(string text)
    {
        var parser = new ScriptParser(Parser.Tokenize(text));
        ListNode list = parser.ParseList();
        if (!parser.AtEnd)
        {
            throw new FormatException("syntax error near '" + parser.Current.Text + "'");
        }

        return list;
    }

    /// <summary>Whether <paramref name="text"/> is a complete command (false: more lines are needed).</summary>
    public static bool IsComplete(string text)
    {
        try
        {
            Parse(text);
            return true;
        }
        catch (Exception e)
        {
            return e is not IncompleteInputException;
        }
    }

    private bool AtEnd => _pos >= _tokens.Count;
    private Token Current => _tokens[_pos];

    private bool IsWord(string text) => !AtEnd && Current.Kind == TokenKind.Word && Current.Text == text;

    private bool IsSeparator => !AtEnd && (Current.Kind == TokenKind.Sequence || Current.Kind == TokenKind.Newline);

    private void SkipNewlines()
    {
        while (!AtEnd && Current.Kind == TokenKind.Newline)
        {
            _pos++;
        }
    }

    private void SkipSeparators()
    {
        while (IsSeparator)
        {
            _pos++;
        }
    }

    private void Expect(string word)
    {
        if (AtEnd)
        {
            throw new IncompleteInputException("expected '" + word + "'");
        }

        if (!IsWord(word))
        {
            throw new FormatException("syntax error: expected '" + word + "' near '" + Current.Text + "'");
        }

        _pos++;
    }

    /// <summary>Parses commands until the input ends or one of <paramref name="stopWords"/> starts a command.</summary>
    private ListNode ParseList(params string[] stopWords)
    {
        var list = new ListNode();
        while (true)
        {
            SkipSeparators();
            if (AtEnd || IsStopWord(stopWords))
            {
                return list;
            }

            list.Items.Add(ParseAndOr());
            if (AtEnd || IsStopWord(stopWords))
            {
                return list;
            }

            if (!IsSeparator)
            {
                throw new FormatException("syntax error near '" + Current.Text + "'");
            }
        }
    }

    private bool IsStopWord(string[] stopWords)
    {
        foreach (string word in stopWords)
        {
            if (IsWord(word))
            {
                return true;
            }
        }

        return false;
    }

    private Node ParseAndOr()
    {
        Node first = ParsePipeline();
        if (AtEnd || (Current.Kind != TokenKind.And && Current.Kind != TokenKind.Or))
        {
            return first;
        }

        var node = new AndOrNode();
        node.Items.Add(first);
        while (!AtEnd && (Current.Kind == TokenKind.And || Current.Kind == TokenKind.Or))
        {
            node.Operators.Add(Current.Kind);
            _pos++;
            SkipNewlines();
            node.Items.Add(ParsePipeline());
        }

        return node;
    }

    private Node ParsePipeline()
    {
        var pipeline = new PipelineNode();
        if (IsWord("!"))
        {
            pipeline.Negate = true;
            _pos++;
        }

        pipeline.Stages.Add(ParseCommand());
        while (!AtEnd && Current.Kind == TokenKind.Pipe)
        {
            _pos++;
            SkipNewlines();
            pipeline.Stages.Add(ParseCommand());
        }

        return pipeline.Stages.Count == 1 && !pipeline.Negate ? pipeline.Stages[0] : pipeline;
    }

    private Node ParseCommand()
    {
        if (AtEnd)
        {
            throw new IncompleteInputException("expected a command");
        }

        Node node;
        string word = Current.Kind == TokenKind.Word ? Current.Text : string.Empty;
        if (word == "if")
        {
            node = ParseIf();
        }
        else if (word == "for")
        {
            node = ParseFor();
        }
        else if (word == "while" || word == "until")
        {
            node = ParseWhile();
        }
        else if (word == "{")
        {
            _pos++;
            node = new GroupNode(ParseList("}"));
            Expect("}");
        }
        else if (word == "function" || (word.Length > 2 && word.EndsWith("()") && IsName(word.Substring(0, word.Length - 2))))
        {
            return ParseFunction();
        }
        else if (s_reserved.Contains(word))
        {
            throw new FormatException("syntax error near '" + word + "'");
        }
        else
        {
            return ParseSimple();
        }

        ParseRedirects(node);
        return node;
    }

    private Node ParseSimple()
    {
        var command = new SimpleCommandNode();
        while (!AtEnd)
        {
            if (Current.Kind == TokenKind.Word)
            {
                command.Words.Add(Current.Text);
                _pos++;
            }
            else if (!TryParseRedirect(command))
            {
                break;
            }
        }

        if (command.Words.Count == 0 && command.Redirects.Count == 0)
        {
            throw new FormatException("syntax error near '" + (AtEnd ? "end of input" : Current.Text) + "'");
        }

        return command;
    }

    private void ParseRedirects(Node node)
    {
        while (!AtEnd && TryParseRedirect(node))
        {
        }
    }

    private bool TryParseRedirect(Node node)
    {
        TokenKind kind = Current.Kind;
        if (kind != TokenKind.RedirectIn && kind != TokenKind.RedirectOut && kind != TokenKind.RedirectAppend)
        {
            return false;
        }

        _pos++;
        if (AtEnd)
        {
            throw new IncompleteInputException("expected a file name after redirection");
        }

        if (Current.Kind != TokenKind.Word)
        {
            throw new FormatException("syntax error: expected a file name near '" + Current.Text + "'");
        }

        node.Redirects.Add(new Redirect(kind, Current.Text));
        _pos++;
        return true;
    }

    private Node ParseIf()
    {
        var node = new IfNode();
        _pos++;   // if
        ListNode condition = ParseList("then");
        Expect("then");
        node.Branches.Add((condition, ParseList("elif", "else", "fi")));

        while (IsWord("elif"))
        {
            _pos++;
            condition = ParseList("then");
            Expect("then");
            node.Branches.Add((condition, ParseList("elif", "else", "fi")));
        }

        if (IsWord("else"))
        {
            _pos++;
            node.Else = ParseList("fi");
        }

        Expect("fi");
        return node;
    }

    private Node ParseFor()
    {
        _pos++;   // for
        if (AtEnd)
        {
            throw new IncompleteInputException("expected a variable name");
        }

        string name = Current.Text;
        if (Current.Kind != TokenKind.Word || !IsName(name))
        {
            throw new FormatException("syntax error: bad for-loop variable '" + name + "'");
        }

        _pos++;
        SkipNewlines();

        List<string>? words = null;
        if (IsWord("in"))
        {
            _pos++;
            words = new List<string>();
            while (!AtEnd && Current.Kind == TokenKind.Word)
            {
                words.Add(Current.Text);
                _pos++;
            }
        }

        SkipSeparators();
        Expect("do");
        ListNode body = ParseList("done");
        Expect("done");
        return new ForNode(name, words, body);
    }

    private Node ParseWhile()
    {
        bool until = Current.Text == "until";
        _pos++;
        ListNode condition = ParseList("do");
        Expect("do");
        ListNode body = ParseList("done");
        Expect("done");
        return new WhileNode(condition, body, until);
    }

    private Node ParseFunction()
    {
        string name;
        if (Current.Text == "function")
        {
            _pos++;
            if (AtEnd)
            {
                throw new IncompleteInputException("expected a function name");
            }

            name = Current.Text;
            if (name.EndsWith("()"))
            {
                name = name.Substring(0, name.Length - 2);
            }

            if (!IsName(name))
            {
                throw new FormatException("syntax error: bad function name '" + name + "'");
            }
        }
        else
        {
            name = Current.Text.Substring(0, Current.Text.Length - 2);
        }

        _pos++;
        SkipNewlines();
        return new FunctionNode(name, ParseCommand());
    }

    public static bool IsName(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
