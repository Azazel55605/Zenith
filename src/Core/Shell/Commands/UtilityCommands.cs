using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Zenith.Core.Shell.Commands;

/// <summary>Smaller coreutils for scripts: seq, printf, basename, dirname, xargs, du, diff, tr, cut, sed.</summary>
internal static class UtilityCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("seq", "seq [first [step]] last", "Print a sequence of numbers", Seq));
        add(new Command("printf", "printf format [args...]", "Format and print (%s %d %%, \\n \\t escapes)", Printf));
        add(new Command("basename", "basename path [suffix]", "Strip directories (and a suffix) from a path", Basename));
        add(new Command("dirname", "dirname path", "Strip the last component from a path", Dirname));
        add(new Command("xargs", "xargs [command [args...]]", "Run a command with arguments read from stdin", Xargs));
        add(new Command("du", "du [-s] [path...]", "Show disk usage of files and directories", Du));
        add(new Command("diff", "diff file1 file2", "Compare two files line by line", Diff));
        add(new Command("tr", "tr [-d] set1 [set2]", "Translate or delete characters (ranges like a-z)", Tr));
        add(new Command("cut", "cut -d delim -f fields | -c chars [file...]", "Print selected fields or characters of each line", Cut));
        add(new Command("sed", "sed [-n] script [file...]", "Stream editor: s/re/text/[g], /re/d, Nd, Np", Sed));
    }

    // --- seq, printf ---

    private static int Seq(CommandContext c)
    {
        var numbers = new List<long>();
        foreach (string arg in c.Args)
        {
            if (!long.TryParse(arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
            {
                return c.Fail("invalid number '" + arg + "'");
            }

            numbers.Add(n);
        }

        long first = 1, step = 1, last;
        switch (numbers.Count)
        {
            case 1: last = numbers[0]; break;
            case 2: first = numbers[0]; last = numbers[1]; break;
            case 3: first = numbers[0]; step = numbers[1]; last = numbers[2]; break;
            default: return c.Fail("usage: seq [first [step]] last");
        }

        if (step == 0)
        {
            return c.Fail("step must not be zero");
        }

        for (long i = first; step > 0 ? i <= last : i >= last; i += step)
        {
            c.WriteLine(i.ToString(CultureInfo.InvariantCulture));
        }

        return 0;
    }

    private static int Printf(CommandContext c)
    {
        if (c.Args.Length == 0)
        {
            return c.Fail("usage: printf format [args...]");
        }

        string format = c.Args[0];
        int next = 1;
        var output = new StringBuilder();

        // Like POSIX printf, the format is reused while arguments remain.
        do
        {
            for (int i = 0; i < format.Length; i++)
            {
                char ch = format[i];
                if (ch == '\\' && i + 1 < format.Length)
                {
                    char e = format[++i];
                    output.Append(e switch { 'n' => '\n', 't' => '\t', '\\' => '\\', _ => e });
                }
                else if (ch == '%' && i + 1 < format.Length)
                {
                    char spec = format[++i];
                    if (spec == '%')
                    {
                        output.Append('%');
                        continue;
                    }

                    string arg = next < c.Args.Length ? c.Args[next++] : string.Empty;
                    if (spec == 'd' || spec == 'i')
                    {
                        output.Append(long.TryParse(arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : 0);
                    }
                    else
                    {
                        output.Append(arg);   // %s and anything else
                    }
                }
                else
                {
                    output.Append(ch);
                }
            }
        }
        while (next < c.Args.Length && format.Contains('%'));

        c.Write(output.ToString());
        return 0;
    }

    // --- paths ---

    private static int Basename(CommandContext c)
    {
        if (c.Args.Length is < 1 or > 2)
        {
            return c.Fail("usage: basename path [suffix]");
        }

        string path = c.Args[0].Length > 1 ? c.Args[0].TrimEnd('/') : c.Args[0];
        string name = path == "/" ? "/" : path.Substring(path.LastIndexOf('/') + 1);
        if (c.Args.Length == 2 && name.EndsWith(c.Args[1]) && name != c.Args[1])
        {
            name = name.Substring(0, name.Length - c.Args[1].Length);
        }

        c.WriteLine(name);
        return 0;
    }

    private static int Dirname(CommandContext c)
    {
        if (c.Args.Length != 1)
        {
            return c.Fail("usage: dirname path");
        }

        string path = c.Args[0].Length > 1 ? c.Args[0].TrimEnd('/') : c.Args[0];
        int slash = path.LastIndexOf('/');
        c.WriteLine(slash < 0 ? "." : slash == 0 ? "/" : path.Substring(0, slash));
        return 0;
    }

    // --- xargs ---

    private static int Xargs(CommandContext c)
    {
        var words = new List<string>();
        foreach (string word in (c.Stdin ?? string.Empty).Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(word);
        }

        var command = new StringBuilder();
        foreach (string part in c.Args.Length > 0 ? c.Args : new[] { "echo" })
        {
            command.Append(Quote(part)).Append(' ');
        }

        foreach (string word in words)
        {
            command.Append(Quote(word)).Append(' ');
        }

        return c.Shell.RunCommandString(command.ToString(), new List<string>(), null, c.Out);
    }

    /// <summary>Single-quotes a word for the shell, so its characters are taken literally.</summary>
    private static string Quote(string word) => "'" + word.Replace("'", "'\\''") + "'";

    // --- du ---

    private static int Du(CommandContext c)
    {
        if (!c.TryParse("sh", out var flags, out var paths))
        {
            return 2;
        }

        if (paths.Count == 0)
        {
            paths.Add(".");
        }

        int status = 0;
        foreach (string arg in paths)
        {
            string path = c.Resolve(arg);
            if (File.Exists(path))
            {
                c.WriteLine(Kilobytes(new FileInfo(path).Length) + "\t" + arg);
            }
            else if (Directory.Exists(path))
            {
                long total = DuDirectory(c, path, arg, flags.Contains('s'));
                c.WriteLine(Kilobytes(total) + "\t" + arg);
            }
            else
            {
                status = c.Fail("cannot access '" + arg + "': No such file or directory");
            }
        }

        return status;
    }

    /// <summary>Total bytes under <paramref name="path"/>, printing each subdirectory unless <paramref name="summaryOnly"/>.</summary>
    private static long DuDirectory(CommandContext c, string path, string shown, bool summaryOnly)
    {
        long total = 0;
        foreach (string file in Directory.GetFiles(path))
        {
            total += new FileInfo(file).Length;
        }

        foreach (string dir in Directory.GetDirectories(path))
        {
            string childShown = shown.TrimEnd('/') + "/" + System.IO.Path.GetFileName(dir);
            long size = DuDirectory(c, dir, childShown, summaryOnly);
            if (!summaryOnly)
            {
                c.WriteLine(Kilobytes(size) + "\t" + childShown);
            }

            total += size;
        }

        return total;
    }

    private static string Kilobytes(long bytes) => ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture);

    // --- diff ---

    /// <summary>Line diff via longest common subsequence, printed in the classic "NcN / < / > / ---" format.</summary>
    private static int Diff(CommandContext c)
    {
        if (c.Args.Length != 2)
        {
            return c.Fail("usage: diff file1 file2");
        }

        string[] texts = new string[2];
        for (int i = 0; i < 2; i++)
        {
            string path = c.Resolve(c.Args[i]);
            if (!File.Exists(path))
            {
                c.Fail(c.Args[i] + ": No such file or directory");
                return 2;
            }

            texts[i] = File.ReadAllText(path);
        }

        List<string> a = CommandContext.Lines(texts[0]), b = CommandContext.Lines(texts[1]);
        int[,] lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
        {
            for (int j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        // Backtrack the LCS table into an edit script: ' ' keep, '-' delete a[i], '+' insert b[j].
        var ops = new List<char>();
        for (int i = 0, j = 0; i < a.Count || j < b.Count;)
        {
            if (i < a.Count && j < b.Count && a[i] == b[j])
            {
                ops.Add(' ');
                i++;
                j++;
            }
            else if (j >= b.Count || (i < a.Count && lcs[i + 1, j] >= lcs[i, j + 1]))
            {
                ops.Add('-');
                i++;
            }
            else
            {
                ops.Add('+');
                j++;
            }
        }

        bool different = false;
        int x = 0, y = 0, k = 0;
        while (k < ops.Count)
        {
            if (ops[k] == ' ')
            {
                x++;
                y++;
                k++;
                continue;
            }

            // One hunk: a run of deletions and insertions between kept lines.
            int x0 = x, y0 = y;
            while (k < ops.Count && ops[k] != ' ')
            {
                if (ops[k++] == '-')
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }

            different = true;
            static string Range(int from, int to) => to - from <= 1 ? (from + 1).ToString() : (from + 1) + "," + to;
            string header = x > x0 && y > y0 ? Range(x0, x) + "c" + Range(y0, y)
                : x > x0 ? Range(x0, x) + "d" + y0
                : x0 + "a" + Range(y0, y);
            c.WriteLine(header);
            for (int i = x0; i < x; i++)
            {
                c.WriteLine(Ansi.Red("< " + a[i]));
            }

            if (x > x0 && y > y0)
            {
                c.WriteLine("---");
            }

            for (int j = y0; j < y; j++)
            {
                c.WriteLine(Ansi.Green("> " + b[j]));
            }
        }

        return different ? 1 : 0;
    }

    // --- tr, cut ---

    private static int Tr(CommandContext c)
    {
        bool delete = c.Args.Length > 0 && c.Args[0] == "-d";
        int first = delete ? 1 : 0;
        if ((delete && c.Args.Length != 2) || (!delete && c.Args.Length != 2))
        {
            return c.Fail("usage: tr [-d] set1 [set2]");
        }

        string set1 = ExpandSet(c.Args[first]);
        string set2 = delete ? string.Empty : ExpandSet(c.Args[first + 1]);
        var output = new StringBuilder();
        foreach (char ch in c.Stdin ?? string.Empty)
        {
            int index = set1.IndexOf(ch);
            if (index < 0)
            {
                output.Append(ch);
            }
            else if (!delete)
            {
                output.Append(set2.Length == 0 ? ch : set2[Math.Min(index, set2.Length - 1)]);
            }
        }

        c.Write(output.ToString());
        return 0;
    }

    /// <summary>Expands ranges and escapes in a tr set: "a-z" → "abc...z", "\n" → newline.</summary>
    private static string ExpandSet(string set)
    {
        var chars = new StringBuilder();
        for (int i = 0; i < set.Length; i++)
        {
            char ch = set[i];
            if (ch == '\\' && i + 1 < set.Length)
            {
                char e = set[++i];
                chars.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
            }
            else if (i + 2 < set.Length && set[i + 1] == '-')
            {
                for (char r = ch; r <= set[i + 2]; r++)
                {
                    chars.Append(r);
                }

                i += 2;
            }
            else
            {
                chars.Append(ch);
            }
        }

        return chars.ToString();
    }

    private static int Cut(CommandContext c)
    {
        string delimiter = "\t";
        string? fields = null, characters = null;
        var files = new List<string>();
        for (int i = 0; i < c.Args.Length; i++)
        {
            string arg = c.Args[i];
            if (arg.StartsWith("-d"))
            {
                delimiter = arg.Length > 2 ? arg.Substring(2) : (++i < c.Args.Length ? c.Args[i] : "\t");
            }
            else if (arg.StartsWith("-f"))
            {
                fields = arg.Length > 2 ? arg.Substring(2) : (++i < c.Args.Length ? c.Args[i] : null);
            }
            else if (arg.StartsWith("-c"))
            {
                characters = arg.Length > 2 ? arg.Substring(2) : (++i < c.Args.Length ? c.Args[i] : null);
            }
            else
            {
                files.Add(arg);
            }
        }

        string? list = fields ?? characters;
        if (list is null || delimiter.Length != 1 || !TryParseList(list, out var ranges))
        {
            return c.Fail("usage: cut -d delim -f fields | -c chars (lists like 1,3-5)");
        }

        foreach (var (_, text) in c.ReadInputs(files))
        {
            foreach (string line in CommandContext.Lines(text))
            {
                if (characters is not null)
                {
                    var picked = new StringBuilder();
                    for (int i = 0; i < line.Length; i++)
                    {
                        if (InRanges(ranges, i + 1))
                        {
                            picked.Append(line[i]);
                        }
                    }

                    c.WriteLine(picked.ToString());
                }
                else if (line.IndexOf(delimiter[0]) < 0)
                {
                    c.WriteLine(line);   // like cut: lines without the delimiter pass through
                }
                else
                {
                    string[] parts = line.Split(delimiter[0]);
                    var picked = new List<string>();
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (InRanges(ranges, i + 1))
                        {
                            picked.Add(parts[i]);
                        }
                    }

                    c.WriteLine(string.Join(delimiter, picked));
                }
            }
        }

        return 0;
    }

    private static bool TryParseList(string list, out List<(int From, int To)> ranges)
    {
        ranges = new List<(int, int)>();
        foreach (string part in list.Split(','))
        {
            string[] bounds = part.Split('-');
            int from = 1, to = int.MaxValue;
            if (bounds.Length > 2
                || (bounds[0].Length > 0 && !int.TryParse(bounds[0], out from))
                || (bounds.Length == 2 && bounds[1].Length > 0 && !int.TryParse(bounds[1], out to)))
            {
                return false;
            }

            if (bounds.Length == 1)
            {
                to = from;
            }

            ranges.Add((from, to));
        }

        return true;
    }

    private static bool InRanges(List<(int From, int To)> ranges, int position)
    {
        foreach (var (from, to) in ranges)
        {
            if (position >= from && position <= to)
            {
                return true;
            }
        }

        return false;
    }

    // --- sed ---

    /// <summary>
    /// A small sed: one or more ';'-separated commands of the forms <c>s/re/repl/[g]</c>,
    /// <c>[addr]d</c> and <c>[addr]p</c>, where addr is a line number or <c>/re/</c>.
    /// <c>-n</c> suppresses the automatic printing of each line. Regexes are .NET syntax;
    /// <c>&amp;</c> and <c>\1</c>.. in the replacement refer to the match and its groups.
    /// </summary>
    private static int Sed(CommandContext c)
    {
        bool quiet = false;
        var rest = new List<string>();
        foreach (string arg in c.Args)
        {
            if (arg == "-n")
            {
                quiet = true;
            }
            else
            {
                rest.Add(arg);
            }
        }

        if (rest.Count == 0)
        {
            return c.Fail("usage: sed [-n] script [file...]");
        }

        List<SedCommand> commands;
        try
        {
            commands = SedCommand.ParseScript(rest[0]);
        }
        catch (FormatException e)
        {
            return c.Fail(e.Message);
        }

        rest.RemoveAt(0);
        int lineNumber = 0;
        foreach (var (_, text) in c.ReadInputs(rest))
        {
            foreach (string original in CommandContext.Lines(text))
            {
                lineNumber++;
                string line = original;
                bool deleted = false;
                foreach (SedCommand command in commands)
                {
                    if (!command.Matches(lineNumber, line))
                    {
                        continue;
                    }

                    if (command.Kind == 'd')
                    {
                        deleted = true;
                        break;
                    }

                    if (command.Kind == 'p')
                    {
                        c.WriteLine(line);
                    }
                    else
                    {
                        line = command.Substitute(line);
                    }
                }

                if (!deleted && !quiet)
                {
                    c.WriteLine(line);
                }
            }
        }

        return 0;
    }

    private sealed class SedCommand
    {
        private int _lineAddress;
        private Regex? _patternAddress;
        private Regex? _search;
        private string _replacement = string.Empty;
        private bool _global;

        public char Kind { get; private set; }

        public bool Matches(int lineNumber, string line)
            => (_lineAddress == 0 || _lineAddress == lineNumber) && (_patternAddress is null || _patternAddress.IsMatch(line));

        public string Substitute(string line)
        {
            // sed's replacement syntax: & is the match, \1.. the groups.
            string replacement = Regex.Replace(_replacement.Replace("$", "$$"), @"\\&|\\(\d)|&", m =>
                m.Value == "&" ? "$0" : m.Value == "\\&" ? "&" : "$" + m.Groups[1].Value);
            return _global ? _search!.Replace(line, replacement) : _search!.Replace(line, replacement, 1);
        }

        public static List<SedCommand> ParseScript(string script)
        {
            var commands = new List<SedCommand>();
            int i = 0;
            while (i < script.Length)
            {
                while (i < script.Length && (script[i] == ';' || script[i] == ' '))
                {
                    i++;
                }

                if (i >= script.Length)
                {
                    break;
                }

                var command = new SedCommand();
                if (char.IsDigit(script[i]))
                {
                    int start = i;
                    while (i < script.Length && char.IsDigit(script[i]))
                    {
                        i++;
                    }

                    command._lineAddress = int.Parse(script.Substring(start, i - start), CultureInfo.InvariantCulture);
                }
                else if (script[i] == '/')
                {
                    command._patternAddress = new Regex(ReadDelimited(script, ref i, '/'));
                }

                if (i >= script.Length)
                {
                    throw new FormatException("missing command");
                }

                command.Kind = script[i++];
                switch (command.Kind)
                {
                    case 'd':
                    case 'p':
                        break;
                    case 's':
                        if (i >= script.Length)
                        {
                            throw new FormatException("unterminated `s' command");
                        }

                        char delimiter = script[i];
                        command._search = new Regex(ReadDelimited(script, ref i, delimiter));
                        i--;   // the closing delimiter of the pattern opens the replacement
                        command._replacement = ReadDelimited(script, ref i, delimiter);
                        while (i < script.Length && script[i] == 'g')
                        {
                            command._global = true;
                            i++;
                        }

                        break;
                    default:
                        throw new FormatException("unknown command: `" + command.Kind + "'");
                }

                commands.Add(command);
            }

            return commands;
        }

        /// <summary>Reads text between delimiters starting at <paramref name="i"/> (on the opening one); leaves i after the closing one.</summary>
        private static string ReadDelimited(string script, ref int i, char delimiter)
        {
            int start = ++i;
            while (i < script.Length && script[i] != delimiter)
            {
                i += script[i] == '\\' ? 2 : 1;
            }

            if (i >= script.Length)
            {
                throw new FormatException("unterminated `" + delimiter + "' in sed script");
            }

            return script.Substring(start, i++ - start);
        }
    }
}
