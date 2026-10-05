using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Shell.Commands;

namespace Zenith.Core.Shell;

/// <summary>
/// A Unix-style command interpreter. It is UI-independent: a host (the terminal window, or a
/// future text console) feeds it lines and gives it an <see cref="IOutput"/> to write to.
/// Each shell has its own working directory and environment.
/// </summary>
internal sealed class Shell
{
    private static readonly Dictionary<string, Command> s_commands = CreateCommandTable();

    private readonly Dictionary<string, string> _environment = new();
    private readonly IOutput _terminal;

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
        if (login)
        {
            RunProfile();
        }
    }

    /// <summary>Every built-in command, by name.</summary>
    public static IReadOnlyDictionary<string, Command> Commands => s_commands;

    /// <summary>Adds a command from outside the core (e.g. the desktop registers <c>fps</c>).</summary>
    public static void Register(Command command) => s_commands[command.Name] = command;

    public string WorkingDirectory { get; private set; }
    public int LastStatus { get; private set; }
    public List<string> History { get; } = new();
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <summary>Opens a GUI application by name; set by the desktop. Returns false for an unknown name.</summary>
    public Func<string, bool>? OpenApp { get; set; }

    /// <summary>Raised by the <c>exit</c> command.</summary>
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

    public string Get(string name)
    {
        if (name == "?")
        {
            return LastStatus.ToString();
        }

        return _environment.TryGetValue(name, out string? value) ? value : string.Empty;
    }

    public void Set(string name, string value) => _environment[name] = value;

    public void Unset(string name) => _environment.Remove(name);

    public void RequestExit() => ExitRequested?.Invoke();

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
        string full = Path.GetFullPath(combined);
        return full.Length > 1 && full.EndsWith('/') ? full.TrimEnd('/') : full;
    }

    /// <summary>Runs one command line and records it in the history.</summary>
    public void Execute(string line)
    {
        if (line.Trim().Length == 0)
        {
            return;
        }

        History.Add(line);
        try
        {
            RunList(Parser.Tokenize(line));
        }
        catch (FormatException e)
        {
            _terminal.Write("sh: " + e.Message + "\n");
            LastStatus = 2;
        }
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
                    string name = Path.GetFileName(entry);
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

    private void RunList(List<Token> tokens)
    {
        var pipeline = new List<Token>();
        TokenKind connector = TokenKind.Sequence;
        for (int i = 0; i <= tokens.Count; i++)
        {
            bool end = i == tokens.Count;
            TokenKind kind = end ? TokenKind.Sequence : tokens[i].Kind;
            if (!end && kind != TokenKind.Sequence && kind != TokenKind.And && kind != TokenKind.Or)
            {
                pipeline.Add(tokens[i]);
                continue;
            }

            if (pipeline.Count > 0)
            {
                bool run = connector == TokenKind.Sequence
                    || (connector == TokenKind.And && LastStatus == 0)
                    || (connector == TokenKind.Or && LastStatus != 0);
                if (run)
                {
                    LastStatus = RunPipeline(pipeline);
                }
            }
            else if (kind != TokenKind.Sequence)
            {
                throw new FormatException("syntax error near '" + tokens[i].Text + "'");
            }

            pipeline.Clear();
            connector = kind;
        }
    }

    private int RunPipeline(List<Token> tokens)
    {
        // Split on '|' into simple commands.
        var stages = new List<List<Token>> { new() };
        foreach (Token token in tokens)
        {
            if (token.Kind == TokenKind.Pipe)
            {
                stages.Add(new List<Token>());
            }
            else
            {
                stages[^1].Add(token);
            }
        }

        string? stdin = null;
        int status = 0;
        for (int i = 0; i < stages.Count; i++)
        {
            bool last = i == stages.Count - 1;
            status = RunSimple(stages[i], ref stdin, last);
        }

        return status;
    }

    /// <summary>Runs one command. Its output becomes <paramref name="stdin"/> for the next stage unless it is the last.</summary>
    private int RunSimple(List<Token> tokens, ref string? stdin, bool last)
    {
        var words = new List<string>();
        string? outputFile = null, inputFile = null;
        bool append = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            if (token.Kind == TokenKind.Word)
            {
                string text = Parser.ExpandWord(token.Text, Get, out bool globbable);
                words.AddRange(globbable ? ExpandGlob(text) : new[] { text });
                continue;
            }

            if (i + 1 >= tokens.Count || tokens[i + 1].Kind != TokenKind.Word)
            {
                throw new FormatException("syntax error: expected a file name after '" + token.Text + "'");
            }

            string target = ResolvePath(Parser.ExpandWord(tokens[++i].Text, Get, out _));
            if (token.Kind == TokenKind.RedirectIn)
            {
                inputFile = target;
            }
            else
            {
                outputFile = target;
                append = token.Kind == TokenKind.RedirectAppend;
            }
        }

        if (words.Count == 0)
        {
            throw new FormatException("syntax error: empty command");
        }

        // NAME=value on its own sets a shell variable.
        if (words.Count == 1 && words[0].IndexOf('=') > 0)
        {
            int eq = words[0].IndexOf('=');
            Set(words[0].Substring(0, eq), words[0].Substring(eq + 1));
            stdin = null;
            return 0;
        }

        if (inputFile is not null)
        {
            if (!File.Exists(inputFile))
            {
                _terminal.Write("sh: " + inputFile + ": No such file or directory\n");
                return 1;
            }

            stdin = File.ReadAllText(inputFile);
        }

        string name = words[0];
        if (!s_commands.TryGetValue(name, out Command? command))
        {
            _terminal.Write("sh: " + name + ": command not found\n");
            stdin = null;
            return 127;
        }

        bool captured = !last || outputFile is not null;
        BufferOutput? buffer = captured ? new BufferOutput() : null;
        var context = new CommandContext(this, name, words.GetRange(1, words.Count - 1).ToArray(), stdin,
            buffer is not null ? buffer : _terminal, _terminal, isTerminal: buffer is null);

        int status;
        try
        {
            status = command.Run(context);
        }
        catch (Exception e)
        {
            status = context.Fail(e.Message);
        }

        stdin = buffer is null ? null : Ansi.Strip(buffer.ToString());
        if (outputFile is not null)
        {
            string text = stdin ?? string.Empty;
            try
            {
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

            stdin = null;
        }

        return status;
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
                string name = Path.GetFileName(entry);
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

    private void RunProfile()
    {
        try
        {
            if (File.Exists("/etc/profile"))
            {
                foreach (string line in File.ReadAllText("/etc/profile").Split('\n'))
                {
                    RunList(Parser.Tokenize(line));
                }
            }
        }
        catch (Exception e)
        {
            _terminal.Write("sh: /etc/profile: " + e.Message + "\n");
        }
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
        FileCommands.Register(Add);
        TextCommands.Register(Add);

        // Commands that need Cosmos (storage, power, memory) are registered by the kernel at
        // boot, which keeps everything in this folder plain .NET and unit-testable on the host.
        return table;
    }
}
