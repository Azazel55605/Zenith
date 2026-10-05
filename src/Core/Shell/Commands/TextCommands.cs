using System;
using System.Collections.Generic;
using System.IO;

namespace Zenith.Core.Shell.Commands;

/// <summary>Text filters. Each reads its named files, or stdin when none are given, so they compose in pipes.</summary>
internal static class TextCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("cat", "cat [file...]", "Print files", Cat));
        add(new Command("head", "head [-n N] [file...]", "Print the first lines", c => HeadTail(c, head: true)));
        add(new Command("tail", "tail [-n N] [file...]", "Print the last lines", c => HeadTail(c, head: false)));
        add(new Command("wc", "wc [-lwc] [file...]", "Count lines, words and bytes", Wc));
        add(new Command("grep", "grep [-inv] pattern [file...]", "Print lines containing a pattern", Grep));
        add(new Command("sort", "sort [-r] [file...]", "Sort lines", Sort));
        add(new Command("uniq", "uniq [file...]", "Drop repeated adjacent lines", Uniq));
        add(new Command("tee", "tee [-a] file", "Copy stdin to a file and to stdout", Tee));
    }

    private static int Cat(CommandContext c)
    {
        foreach (var (_, text) in c.ReadInputs(new List<string>(c.Args)))
        {
            c.Write(text);
        }

        return 0;
    }

    /// <summary>Takes <c>-n N</c> or <c>-N</c> off the argument list; the rest are files.</summary>
    private static bool TryLineCount(CommandContext c, out int count, out List<string> files)
    {
        count = 10;
        files = new List<string>();
        for (int i = 0; i < c.Args.Length; i++)
        {
            string arg = c.Args[i];
            if (arg == "-n" && i + 1 < c.Args.Length)
            {
                if (!CommandContext.TryParseCount(c.Args[++i], out count))
                {
                    c.Fail("invalid number of lines: '" + c.Args[i] + "'");
                    return false;
                }
            }
            else if (arg.Length > 1 && arg[0] == '-' && CommandContext.TryParseCount(arg.Substring(1), out int n))
            {
                count = n;
            }
            else
            {
                files.Add(arg);
            }
        }

        return true;
    }

    private static int HeadTail(CommandContext c, bool head)
    {
        if (!TryLineCount(c, out int count, out var files))
        {
            return 2;
        }

        foreach (var (name, text) in c.ReadInputs(files))
        {
            if (files.Count > 1)
            {
                c.WriteLine("==> " + name + " <==");
            }

            List<string> lines = CommandContext.Lines(text);
            int start = head ? 0 : Math.Max(0, lines.Count - count);
            int end = head ? Math.Min(count, lines.Count) : lines.Count;
            for (int i = start; i < end; i++)
            {
                c.WriteLine(lines[i]);
            }
        }

        return 0;
    }

    private static int Wc(CommandContext c)
    {
        if (!c.TryParse("lwc", out var flags, out var files))
        {
            return 2;
        }

        bool all = flags.Count == 0;

        // Like GNU wc: a single count read from stdin is printed bare, so `$(ls | wc -l)` is a number.
        bool bare = !all && flags.Count == 1 && files.Count == 0;
        long totalLines = 0, totalWords = 0, totalBytes = 0;
        int inputs = 0;
        foreach (var (name, text) in c.ReadInputs(files))
        {
            long lines = 0, words = 0;
            bool inWord = false;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    lines++;
                }

                bool space = ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r';
                if (!space && !inWord)
                {
                    words++;
                }

                inWord = !space;
            }

            long bytes = text.Length;
            totalLines += lines;
            totalWords += words;
            totalBytes += bytes;
            inputs++;
            c.WriteLine(Format(lines, words, bytes) + (files.Count > 0 ? " " + name : ""));
        }

        if (inputs > 1)
        {
            c.WriteLine(Format(totalLines, totalWords, totalBytes) + " total");
        }

        return 0;

        string Format(long l, long w, long b)
        {
            if (bare)
            {
                return (flags.Contains('l') ? l : flags.Contains('w') ? w : b).ToString();
            }

            string s = "";
            if (all || flags.Contains('l')) s += l.ToString().PadLeft(7);
            if (all || flags.Contains('w')) s += w.ToString().PadLeft(8);
            if (all || flags.Contains('c')) s += b.ToString().PadLeft(8);
            return s;
        }
    }

    private static int Grep(CommandContext c)
    {
        if (!c.TryParse("inv", out var flags, out var operands))
        {
            return 2;
        }

        if (operands.Count == 0)
        {
            return c.Fail("usage: grep [-inv] pattern [file...]");
        }

        string pattern = operands[0];
        operands.RemoveAt(0);
        var comparison = flags.Contains('i') ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool found = false;

        foreach (var (name, text) in c.ReadInputs(operands))
        {
            List<string> lines = CommandContext.Lines(text);
            for (int i = 0; i < lines.Count; i++)
            {
                int at = lines[i].IndexOf(pattern, comparison);
                if ((at >= 0) == flags.Contains('v'))
                {
                    continue;
                }

                found = true;
                string prefix = (operands.Count > 1 ? Ansi.Cyan(name) + ":" : "") + (flags.Contains('n') ? Ansi.Green((i + 1).ToString()) + ":" : "");
                string line = at >= 0 && !flags.Contains('v') && pattern.Length > 0
                    ? lines[i].Substring(0, at) + Ansi.Red(lines[i].Substring(at, pattern.Length)) + lines[i].Substring(at + pattern.Length)
                    : lines[i];
                c.WriteLine(prefix + line);
            }
        }

        return found ? 0 : 1;
    }

    private static int Sort(CommandContext c)
    {
        if (!c.TryParse("r", out var flags, out var files))
        {
            return 2;
        }

        var all = new List<string>();
        foreach (var (_, text) in c.ReadInputs(files))
        {
            all.AddRange(CommandContext.Lines(text));
        }

        all.Sort(StringComparer.Ordinal);
        if (flags.Contains('r'))
        {
            all.Reverse();
        }

        foreach (string line in all)
        {
            c.WriteLine(line);
        }

        return 0;
    }

    private static int Uniq(CommandContext c)
    {
        string? previous = null;
        foreach (var (_, text) in c.ReadInputs(new List<string>(c.Args)))
        {
            foreach (string line in CommandContext.Lines(text))
            {
                if (line != previous)
                {
                    c.WriteLine(line);
                }

                previous = line;
            }
        }

        return 0;
    }

    private static int Tee(CommandContext c)
    {
        if (!c.TryParse("a", out var flags, out var files) || files.Count != 1)
        {
            return c.Fail("usage: tee [-a] file");
        }

        string text = c.Stdin ?? string.Empty;
        string path = c.Resolve(files[0]);
        if (flags.Contains('a'))
        {
            File.AppendAllText(path, text);
        }
        else
        {
            File.WriteAllText(path, text);
        }

        c.Write(text);
        return 0;
    }
}
