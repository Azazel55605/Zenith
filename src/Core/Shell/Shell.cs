using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Shell.Commands;

namespace Zenith.Core.Shell;

/// <summary>
/// A Unix-style command interpreter. It is UI-independent: a host (the terminal window, or a
/// future text console) feeds it text and gives it an <see cref="IOutput"/> to write to.
/// Each shell has its own working directory, variables, functions and positional parameters.
/// <para>
/// Text is parsed by <see cref="ScriptParser"/> into a syntax tree and run by <see cref="Exec"/>.
/// Control flow (<c>break</c>, <c>continue</c>, <c>return</c>, <c>exit</c>) and Ctrl+C travel as
/// flags checked between commands, not as exceptions.
/// </para>
/// </summary>
internal sealed class Shell : IExpansionContext
{
    private static readonly Dictionary<string, Command> s_commands = CreateCommandTable();

    private readonly Dictionary<string, string> _environment = new();
    private readonly Dictionary<string, Node> _functions = new();
    private readonly IOutput _terminal;
    private readonly Shell? _parent;
    private List<string> _positional = new();
    private string _scriptName = "sh";
    private volatile bool _cancelRequested;

    private Control _control;
    private int _controlLevels;
    private int _controlStatus;
    private int _loopDepth;
    private int _functionDepth;

    private enum Control
    {
        None,
        Break,
        Continue,
        Return,
        Exit,
    }

    /// <param name="terminal">Where the shell and its commands print.</param>
    /// <param name="login">Read <c>/etc/hostname</c> and run <c>/etc/profile</c>, like a login shell.
    /// Off in host-side tests, which must not read the build machine's <c>/etc</c>.</param>
    public Shell(IOutput terminal, bool login = true)
    {
        _terminal = terminal;

        string home = "/home/user";
        _environment["HOME"] = home;
        _environment["USER"] = "user";
        _environment["SHELL"] = "/bin/sh";
        _environment["TERM"] = "zenith";
        _environment["PATH"] = "/bin";
        _environment["HOSTNAME"] = login ? ReadHostname() : "zenith";

        WorkingDirectory = Directory.Exists(home) ? home : "/";
        _environment["PWD"] = WorkingDirectory;
        if (login && File.Exists("/etc/profile"))
        {
            Source("/etc/profile", new List<string>(), null);
        }
    }

    /// <summary>A child shell for a script: copies variables and the directory, shares the terminal and Ctrl+C.</summary>
    private Shell(Shell parent)
    {
        _parent = parent;
        _terminal = parent._terminal;
        foreach (var (name, value) in parent._environment)
        {
            _environment[name] = value;
        }

        WorkingDirectory = parent.WorkingDirectory;
        ReadInputLine = parent.ReadInputLine;
        OpenApp = parent.OpenApp;
    }

    /// <summary>Every built-in command, by name.</summary>
    public static IReadOnlyDictionary<string, Command> Commands => s_commands;

    /// <summary>Adds a command from outside the core (e.g. the desktop registers <c>fps</c>).</summary>
    public static void Register(Command command) => s_commands[command.Name] = command;

    public string WorkingDirectory { get; private set; }
    public int LastStatus { get; private set; }
    public List<string> History { get; } = new();
    public IReadOnlyDictionary<string, string> Environment => _environment;
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>The root terminal: commands writing here are writing to a TTY.</summary>
    public IOutput Terminal => _terminal;

    /// <summary>Opens a GUI application by name; set by the desktop. Returns false for an unknown name.</summary>
    public Func<string, bool>? OpenApp { get; set; }

    /// <summary>Reads a line the user types (for <c>read</c> with no redirected input); null at end of input.</summary>
    public Func<string, string?>? ReadInputLine { get; set; }

    /// <summary>Raised by <c>exit</c> in the interactive shell.</summary>
    public event Action? ExitRequested;

    /// <summary>The prompt, e.g. <c>user@zenith:~/docs$ </c>, with ANSI colors.</summary>
    public string Prompt
    {
        get
        {
            string home = Get("HOME");
            string dir = WorkingDirectory == home ? "~"
                : WorkingDirectory.StartsWith(home + "/") ? "~" + WorkingDirectory.Substring(home.Length)
                : WorkingDirectory;
            return Ansi.Green(Get("USER") + "@" + Get("HOSTNAME")) + ":" + Ansi.Blue(dir) + "$ ";
        }
    }

    /// <summary>Shown while a multi-line command is being entered.</summary>
    public string ContinuationPrompt => Ansi.Dim("> ");

    public string Get(string name)
    {
        switch (name)
        {
            case "?":
                return LastStatus.ToString();
            case "#":
                return _positional.Count.ToString();
            case "0":
                return _scriptName;
        }

        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            int index = name[0] - '1';
            return index < _positional.Count ? _positional[index] : string.Empty;
        }

        return _environment.TryGetValue(name, out string? value) ? value : string.Empty;
    }

    public void Set(string name, string value) => _environment[name] = value;

    public void Unset(string name) => _environment.Remove(name);

    /// <summary>Asks the interactive shell's host to close (<c>exit</c> at the prompt).</summary>
    public void RequestExit() => ExitRequested?.Invoke();

    /// <summary>
    /// Asks the running command line to stop (Ctrl+C). Safe to call from another thread: the
    /// command notices at its next output write or <see cref="ThrowIfCancelled"/> check, and the
    /// line ends with status 130, like a shell killed by SIGINT.
    /// </summary>
    public void Cancel() => _cancelRequested = true;

    public bool IsCancelled => _cancelRequested || (_parent?.IsCancelled ?? false);

    public void ThrowIfCancelled()
    {
        if (IsCancelled)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>Changes the working directory; returns false if it does not exist.</summary>
    public bool ChangeDirectory(string path)
    {
        string full = ResolvePath(path);
        if (!Directory.Exists(full))
        {
            return false;
        }

        WorkingDirectory = full;
        _environment["PWD"] = full;
        return true;
    }

    /// <summary>Turns a path relative to the working directory into a normalized absolute path.</summary>
    public string ResolvePath(string path)
    {
        if (path.Length == 0)
        {
            return WorkingDirectory;
        }

        string combined = path[0] == '/' ? path : (WorkingDirectory == "/" ? "/" : WorkingDirectory + "/") + path;
        string full = System.IO.Path.GetFullPath(combined);
        return full.Length > 1 && full.EndsWith('/') ? full.TrimEnd('/') : full;
    }

    /// <summary>Whether <paramref name="text"/> is a complete command; false means the host should ask for more lines.</summary>
    public static bool IsComplete(string text) => ScriptParser.IsComplete(text);

    /// <summary>Runs text typed at the prompt (one or more lines) and records it in the history.</summary>
    public void Execute(string text)
    {
        if (text.Trim().Length == 0)
        {
            return;
        }

        History.Add(text);
        RunText(text, null, _terminal);

        if (_control == Control.Exit)
        {
            LastStatus = _controlStatus;
            _control = Control.None;
            RequestExit();
        }

        _control = Control.None;

        // The cancel flag is cleared when a line finishes, not when it starts, so a Ctrl+C that
        // arrives before the worker thread gets going still stops it.
        _cancelRequested = false;
    }

    /// <summary>Parses and runs <paramref name="text"/> in this shell, reporting syntax errors like sh.</summary>
    private void RunText(string text, ShellInput? input, IOutput output)
    {
        try
        {
            ListNode script = ScriptParser.Parse(text);
            LastStatus = Exec(script, input, output);
        }
        catch (Exception e)
        {
            // One catch-all dispatching with `is`: on Cosmos a typed catch clause listed before
            // `catch (Exception)` did not get selected for OperationCanceledException.
            if (IsCancelled || e is OperationCanceledException)
            {
                LastStatus = 130;   // 128 + SIGINT
            }
            else
            {
                _terminal.Write("sh: " + (e is IncompleteInputException ? "unexpected end of input (" + e.Message + ")" : e.Message) + "\n");
                LastStatus = e is FormatException ? 2 : 1;
            }
        }

        if (IsCancelled)
        {
            LastStatus = 130;
        }
    }

    /// <summary>Runs a script file in this shell (<c>source</c>), with its own positional parameters if given.</summary>
    public int Source(string path, List<string> args, ShellInput? input, IOutput? output = null)
    {
        List<string> savedPositional = _positional;
        if (args.Count > 0)
        {
            _positional = args;
        }

        try
        {
            RunText(File.ReadAllText(path), input, output ?? _terminal);
            if (_control == Control.Return)
            {
                LastStatus = _controlStatus;
                _control = Control.None;
            }

            return LastStatus;
        }
        finally
        {
            _positional = savedPositional;
        }
    }

    /// <summary>Runs a script file in a child shell (<c>sh script</c>, or a script found on <c>PATH</c>).</summary>
    public int RunScript(string path, List<string> args, ShellInput? input, IOutput output)
    {
        var child = new Shell(this) { _positional = args, _scriptName = path };
        child.RunText(File.ReadAllText(path), input, output);
        return child._control == Control.Exit ? child._controlStatus : child.LastStatus;
    }

    /// <summary>Runs a command string in a child shell (<c>sh -c</c>).</summary>
    public int RunCommandString(string text, List<string> args, ShellInput? input, IOutput output)
    {
        var child = new Shell(this) { _positional = args };
        child.RunText(text, input, output);
        return child._control == Control.Exit ? child._controlStatus : child.LastStatus;
    }

    // --- control-flow requests from built-ins ---

    public int RequestBreak(int levels, bool isContinue)
    {
        if (_loopDepth == 0)
        {
            return 0;   // like sh: break/continue outside a loop does nothing
        }

        _control = isContinue ? Control.Continue : Control.Break;
        _controlLevels = Math.Min(Math.Max(levels, 1), _loopDepth);
        return 0;
    }

    public int RequestReturn(int status)
    {
        _control = Control.Return;
        _controlStatus = status;
        return status;
    }

    public int RequestScriptExit(int status)
    {
        _control = Control.Exit;
        _controlStatus = status;
        return status;
    }

    /// <summary>Drops the first <paramref name="count"/> positional parameters (<c>shift</c>).</summary>
    public bool Shift(int count)
    {
        if (count > _positional.Count)
        {
            return false;
        }

        _positional = _positional.GetRange(count, _positional.Count - count);
        return true;
    }

    public bool HasFunction(string name) => _functions.ContainsKey(name);

    // --- IExpansionContext ---

    string IExpansionContext.Substitute(string command)
    {
        var output = new BufferOutput();
        Control saved = _control;
        RunText(command, null, output);
        _control = saved;
        return Ansi.Strip(output.ToString());
    }

    // --- interpreter ---

    private bool ShouldStop => _control != Control.None || IsCancelled;

    /// <summary>Runs one node with the given input and output, applying its redirections first.</summary>
    private int Exec(Node node, ShellInput? input, IOutput output)
    {
        if (node.Redirects.Count == 0)
        {
            return ExecCore(node, input, output);
        }

        string? outputFile = null;
        bool append = false;
        foreach (Redirect redirect in node.Redirects)
        {
            string target = ResolvePath(Parser.ExpandWord(redirect.Target, this, out _));
            if (redirect.Kind == TokenKind.RedirectIn)
            {
                if (!File.Exists(target))
                {
                    _terminal.Write("sh: " + redirect.Target + ": No such file or directory\n");
                    return 1;
                }

                input = new ShellInput(File.ReadAllText(target));
            }
            else
            {
                outputFile = target;
                append = redirect.Kind == TokenKind.RedirectAppend;
            }
        }

        if (outputFile is null)
        {
            return ExecCore(node, input, output);
        }

        var buffer = new BufferOutput();
        int status = ExecCore(node, input, buffer);
        try
        {
            string text = Ansi.Strip(buffer.ToString());
            if (append)
            {
                File.AppendAllText(outputFile, text);
            }
            else
            {
                File.WriteAllText(outputFile, text);
            }
        }
        catch (Exception e)
        {
            _terminal.Write("sh: " + outputFile + ": " + e.Message + "\n");
            status = 1;
        }

        return status;
    }

    private int ExecCore(Node node, ShellInput? input, IOutput output)
    {
        switch (node)
        {
            case SimpleCommandNode simple:
                return ExecSimple(simple, input, output);
            case PipelineNode pipeline:
                return ExecPipeline(pipeline, input, output);
            case AndOrNode andOr:
                return ExecAndOr(andOr, input, output);
            case ListNode list:
                return ExecList(list, input, output);
            case IfNode ifNode:
                return ExecIf(ifNode, input, output);
            case ForNode forNode:
                return ExecFor(forNode, input, output);
            case WhileNode whileNode:
                return ExecWhile(whileNode, input, output);
            case GroupNode group:
                return ExecList(group.Body, input, output);
            case FunctionNode function:
                _functions[function.Name] = function.Body;
                return 0;
            default:
                throw new InvalidOperationException("unknown syntax node");
        }
    }

    private int ExecList(ListNode list, ShellInput? input, IOutput output)
    {
        int status = 0;
        foreach (Node item in list.Items)
        {
            status = Exec(item, input, output);
            LastStatus = status;
            if (ShouldStop)
            {
                break;
            }
        }

        return status;
    }

    private int ExecAndOr(AndOrNode node, ShellInput? input, IOutput output)
    {
        int status = Exec(node.Items[0], input, output);
        for (int i = 1; i < node.Items.Count && !ShouldStop; i++)
        {
            LastStatus = status;
            bool run = node.Operators[i - 1] == TokenKind.And ? status == 0 : status != 0;
            if (run)
            {
                status = Exec(node.Items[i], input, output);
            }
        }

        return status;
    }

    private int ExecPipeline(PipelineNode node, ShellInput? input, IOutput output)
    {
        int status = 0;
        for (int i = 0; i < node.Stages.Count && !IsCancelled; i++)
        {
            bool last = i == node.Stages.Count - 1;
            BufferOutput? buffer = last ? null : new BufferOutput();
            status = Exec(node.Stages[i], input, buffer is null ? output : buffer);
            if (buffer is not null)
            {
                input = new ShellInput(Ansi.Strip(buffer.ToString()));
            }
        }

        return node.Negate ? (status == 0 ? 1 : 0) : status;
    }

    private int ExecIf(IfNode node, ShellInput? input, IOutput output)
    {
        foreach (var (condition, body) in node.Branches)
        {
            int conditionStatus = ExecList(condition, input, output);
            if (ShouldStop)
            {
                return conditionStatus;
            }

            if (conditionStatus == 0)
            {
                return ExecList(body, input, output);
            }
        }

        return node.Else is null ? 0 : ExecList(node.Else, input, output);
    }

    private int ExecFor(ForNode node, ShellInput? input, IOutput output)
    {
        var values = new List<string>();
        if (node.Words is null)
        {
            values.AddRange(_positional);
        }
        else
        {
            foreach (string raw in node.Words)
            {
                values.AddRange(ExpandArgument(raw));
            }
        }

        int status = 0;
        _loopDepth++;
        try
        {
            foreach (string value in values)
            {
                if (IsCancelled)
                {
                    break;
                }

                Set(node.Variable, value);
                status = ExecList(node.Body, input, output);
                if (!ContinueLoop())
                {
                    break;
                }
            }
        }
        finally
        {
            _loopDepth--;
        }

        return status;
    }

    private int ExecWhile(WhileNode node, ShellInput? input, IOutput output)
    {
        int status = 0;
        _loopDepth++;
        try
        {
            while (!IsCancelled)
            {
                int condition = ExecList(node.Condition, input, output);
                if (ShouldStop)
                {
                    if (!ContinueLoop())
                    {
                        break;
                    }

                    continue;
                }

                if ((condition == 0) == node.Until)
                {
                    break;
                }

                status = ExecList(node.Body, input, output);
                if (!ContinueLoop())
                {
                    break;
                }
            }
        }
        finally
        {
            _loopDepth--;
        }

        return status;
    }

    /// <summary>Consumes a pending break/continue after a loop body; returns false when the loop must end.</summary>
    private bool ContinueLoop()
    {
        if (IsCancelled)
        {
            return false;
        }

        switch (_control)
        {
            case Control.None:
                return true;
            case Control.Continue when _controlLevels <= 1:
                _control = Control.None;
                return true;
            case Control.Break when _controlLevels <= 1:
                _control = Control.None;
                return false;
            case Control.Break:
            case Control.Continue:
                _controlLevels--;   // an outer loop handles it
                return false;
            default:
                return false;       // return / exit propagate
        }
    }

    private int ExecSimple(SimpleCommandNode node, ShellInput? input, IOutput output)
    {
        ThrowIfCancelled();

        // Leading NAME=value words are assignments. (Unlike POSIX they persist even when a
        // command follows, since there are no child processes to scope them to.)
        int first = 0;
        while (first < node.Words.Count && IsAssignment(node.Words[first]))
        {
            string raw = node.Words[first];
            int eq = raw.IndexOf('=');
            Set(raw.Substring(0, eq), Parser.ExpandWord(raw.Substring(eq + 1), this, out _));
            first++;
        }

        var words = new List<string>();
        for (int i = first; i < node.Words.Count; i++)
        {
            words.AddRange(ExpandArgument(node.Words[i]));
        }

        if (words.Count == 0)
        {
            return 0;
        }

        string name = words[0];
        var args = words.GetRange(1, words.Count - 1);

        if (_functions.TryGetValue(name, out Node? body))
        {
            return CallFunction(body, args, input, output);
        }

        if (s_commands.TryGetValue(name, out Command? command))
        {
            return RunCommand(command, name, args, input, output);
        }

        string? script = FindScript(name);
        if (script is not null)
        {
            return RunScript(script, args, input, output);
        }

        _terminal.Write("sh: " + name + ": command not found\n");
        return 127;
    }

    private int RunCommand(Command command, string name, List<string> args, ShellInput? input, IOutput output)
    {
        var context = new CommandContext(this, name, args.ToArray(), input, output, _terminal, isTerminal: output == _terminal);
        try
        {
            return command.Run(context);
        }
        catch (Exception e)
        {
            // A cancelled command is not an error: no message, and the caller ends the line.
            return IsCancelled ? 130 : context.Fail(e.Message);
        }
    }

    private int CallFunction(Node body, List<string> args, ShellInput? input, IOutput output)
    {
        if (_functionDepth >= 100)
        {
            _terminal.Write("sh: function nesting too deep\n");
            return 1;
        }

        List<string> saved = _positional;
        _positional = args;
        _functionDepth++;
        try
        {
            int status = Exec(body, input, output);
            if (_control == Control.Return)
            {
                status = _controlStatus;
                _control = Control.None;
            }

            return status;
        }
        finally
        {
            _functionDepth--;
            _positional = saved;
        }
    }

    /// <summary>A path (anything with a '/') or a file in one of the <c>PATH</c> directories.</summary>
    private string? FindScript(string name)
    {
        if (name.Contains('/'))
        {
            string path = ResolvePath(name);
            return File.Exists(path) ? path : null;
        }

        foreach (string dir in Get("PATH").Split(':'))
        {
            if (dir.Length > 0)
            {
                string path = ResolvePath(dir) + "/" + name;
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>Expands one word into arguments: field splitting, then globbing of unquoted wildcards.</summary>
    private List<string> ExpandArgument(string raw)
    {
        List<string> fields = Parser.ExpandFields(raw, this, out bool globbable);
        if (!globbable)
        {
            return fields;
        }

        var result = new List<string>();
        foreach (string field in fields)
        {
            result.AddRange(ExpandGlob(field));
        }

        return result;
    }

    private static bool IsAssignment(string raw)
    {
        int eq = raw.IndexOf('=');
        return eq > 0 && ScriptParser.IsName(raw.Substring(0, eq));
    }

    /// <summary>
    /// Completes the word before the cursor: a command name for the first word, a path
    /// otherwise. Returns the text to insert (possibly empty) and the candidates when ambiguous.
    /// </summary>
    public string Complete(string line, out List<string> candidates)
    {
        int start = line.LastIndexOf(' ') + 1;
        string word = line.Substring(start);
        bool isCommand = line.Substring(0, start).Trim().Length == 0;
        candidates = new List<string>();

        if (isCommand)
        {
            foreach (string name in s_commands.Keys)
            {
                if (name.StartsWith(word))
                {
                    candidates.Add(name);
                }
            }

            foreach (string name in _functions.Keys)
            {
                if (name.StartsWith(word) && !candidates.Contains(name))
                {
                    candidates.Add(name);
                }
            }
        }
        else
        {
            int slash = word.LastIndexOf('/');
            string dirPart = slash >= 0 ? word.Substring(0, slash + 1) : string.Empty;
            string prefix = word.Substring(slash + 1);
            string dir = ResolvePath(dirPart.Length == 0 ? "." : dirPart);
            if (Directory.Exists(dir))
            {
                foreach (string entry in Directory.GetFileSystemEntries(dir))
                {
                    string name = System.IO.Path.GetFileName(entry);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(Directory.Exists(entry) ? name + "/" : name);
                    }
                }
            }

            word = prefix;
        }

        candidates.Sort(StringComparer.Ordinal);
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        string common = candidates[0];
        foreach (string candidate in candidates)
        {
            int n = 0;
            while (n < common.Length && n < candidate.Length && char.ToLowerInvariant(common[n]) == char.ToLowerInvariant(candidate[n]))
            {
                n++;
            }

            common = common.Substring(0, n);
        }

        string insert = common.Length > word.Length ? common.Substring(word.Length) : string.Empty;
        if (candidates.Count == 1 && !insert.EndsWith('/'))
        {
            insert += " ";
        }

        return insert;
    }

    /// <summary>Expands an unquoted wildcard word against the file system; a pattern that matches nothing stays literal.</summary>
    private IEnumerable<string> ExpandGlob(string text)
    {
        int slash = text.LastIndexOf('/');
        string dirPart = slash >= 0 ? text.Substring(0, slash + 1) : string.Empty;
        string pattern = text.Substring(slash + 1);
        string dir = ResolvePath(dirPart.Length == 0 ? "." : dirPart);

        var matches = new List<string>();
        if (dirPart.IndexOf('*') < 0 && dirPart.IndexOf('?') < 0 && Directory.Exists(dir))
        {
            foreach (string entry in Directory.GetFileSystemEntries(dir))
            {
                string name = System.IO.Path.GetFileName(entry);
                if (Parser.GlobMatch(pattern, name))
                {
                    matches.Add(dirPart + name);
                }
            }
        }

        if (matches.Count == 0)
        {
            matches.Add(text);
        }

        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    private static string ReadHostname()
    {
        try
        {
            return File.Exists("/etc/hostname") ? File.ReadAllText("/etc/hostname").Trim() : "zenith";
        }
        catch (IOException)
        {
            return "zenith";
        }
    }

    private static Dictionary<string, Command> CreateCommandTable()
    {
        var table = new Dictionary<string, Command>();
        void Add(Command c) => table[c.Name] = c;

        BuiltinCommands.Register(Add);
        ScriptCommands.Register(Add);
        FileCommands.Register(Add);
        TextCommands.Register(Add);

        // Commands that need Cosmos (storage, power, memory) are registered by the kernel at
        // boot, which keeps everything in this folder plain .NET and unit-testable on the host.
        return table;
    }
}
