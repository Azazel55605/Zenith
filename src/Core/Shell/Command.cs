using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Shell;

/// <summary>Where a command writes text: the terminal, a pipe buffer, or a file being redirected to.</summary>
internal interface IOutput
{
    void Write(string text);
}

/// <summary>Collects output in memory (pipes and redirections).</summary>
internal sealed class BufferOutput : IOutput
{
    private readonly StringBuilder _text = new();

    public void Write(string text) => _text.Append(text);

    public override string ToString() => _text.ToString();
}

internal delegate int CommandHandler(CommandContext context);

/// <summary>A built-in command. There is no program loader yet, so every command is compiled into the kernel.</summary>
internal sealed class Command
{
    public Command(string name, string usage, string summary, CommandHandler run)
    {
        Name = name;
        Usage = usage;
        Summary = summary;
        Run = run;
    }

    public string Name { get; }
    public string Usage { get; }
    public string Summary { get; }
    public CommandHandler Run { get; }
}

/// <summary>Everything one command invocation sees: its arguments, standard streams and the shell.</summary>
internal sealed class CommandContext
{
    public CommandContext(Shell shell, string name, string[] args, string? stdin, IOutput stdout, IOutput stderr, bool isTerminal)
    {
        IsTerminal = isTerminal;
        Shell = shell;
        Name = name;
        Args = args;
        Stdin = stdin;
        Out = stdout;
        Err = stderr;
    }

    public Shell Shell { get; }
    public string Name { get; }

    /// <summary>Arguments after the command name, already expanded (variables, ~, globs).</summary>
    public string[] Args { get; }

    /// <summary>Piped or redirected input, or null when the command reads from nothing.</summary>
    public string? Stdin { get; }

    public IOutput Out { get; }

    /// <summary>
    /// Whether stdout is the terminal (Unix <c>isatty</c>). Commands use it to pick a layout,
    /// e.g. <c>ls</c> prints one name per line into a pipe. Colors need no check: the shell
    /// strips them from anything that is not the terminal.
    /// </summary>
    public bool IsTerminal { get; }
    public IOutput Err { get; }

    public void Write(string text) => Out.Write(text);

    public void WriteLine(string text = "") => Out.Write(text + "\n");

    /// <summary>Prints "name: message" to stderr and returns exit status 1.</summary>
    public int Fail(string message)
    {
        Err.Write(Name + ": " + message + "\n");
        return 1;
    }

    public string Resolve(string path) => Shell.ResolvePath(path);

    /// <summary>
    /// Splits arguments into single-letter flags and operands, Unix style: <c>-la</c> is
    /// <c>-l -a</c>, and <c>--</c> ends option parsing. Unknown flags are reported.
    /// </summary>
    public bool TryParse(string allowedFlags, out HashSet<char> flags, out List<string> operands)
    {
        flags = new HashSet<char>();
        operands = new List<string>();
        bool optionsDone = false;
        foreach (string arg in Args)
        {
            if (optionsDone || arg.Length < 2 || arg[0] != '-')
            {
                operands.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                optionsDone = true;
                continue;
            }

            for (int i = 1; i < arg.Length; i++)
            {
                if (allowedFlags.IndexOf(arg[i]) < 0)
                {
                    Fail("invalid option -- '" + arg[i] + "'");
                    return false;
                }

                flags.Add(arg[i]);
            }
        }

        return true;
    }

    /// <summary>
    /// The text a filter command works on: each named file in turn, or stdin when no file is
    /// named (or the name is "-"). Unreadable files are reported and skipped.
    /// </summary>
    public IEnumerable<(string Name, string Text)> ReadInputs(List<string> files)
    {
        if (files.Count == 0)
        {
            yield return ("(stdin)", Stdin ?? string.Empty);
            yield break;
        }

        foreach (string file in files)
        {
            if (file == "-")
            {
                yield return ("(stdin)", Stdin ?? string.Empty);
                continue;
            }

            string path = Resolve(file);
            string? text = null;
            if (Directory.Exists(path))
            {
                Fail(file + ": Is a directory");
            }
            else if (!File.Exists(path))
            {
                Fail(file + ": No such file or directory");
            }
            else
            {
                text = File.ReadAllText(path);
            }

            if (text is not null)
            {
                yield return (file, text);
            }
        }
    }

    /// <summary>Splits text into lines without the trailing empty line a final newline would produce.</summary>
    public static List<string> Lines(string text)
    {
        var lines = new List<string>(text.Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    public static bool TryParseCount(string text, out int value) => int.TryParse(text, out value) && value >= 0;
}

/// <summary>ANSI SGR helpers; the terminal understands the 16 basic colors, bold and reset.</summary>
internal static class Ansi
{
    public const string Reset = "\u001b[0m";
    public const string ClearScreen = "\u001b[2J\u001b[H";

    public static string Bold(string s) => "\u001b[1m" + s + Reset;
    public static string Red(string s) => "\u001b[31m" + s + Reset;
    public static string Green(string s) => "\u001b[32m" + s + Reset;
    public static string Yellow(string s) => "\u001b[33m" + s + Reset;
    public static string Blue(string s) => "\u001b[94m" + s + Reset;
    public static string Cyan(string s) => "\u001b[36m" + s + Reset;
    public static string Dim(string s) => "\u001b[90m" + s + Reset;

    public static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && !char.IsLetter(s[i]))
                {
                    i++;
                }

                continue;
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }
}
