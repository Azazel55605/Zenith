using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Shell.Commands;

/// <summary>File and directory commands, coreutils style, on top of the standard System.IO API.</summary>
internal static class FileCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("ls", "ls [-la1] [path...]", "List directory contents", Ls));
        add(new Command("touch", "touch file...", "Create files or update their timestamp", Touch));
        add(new Command("mkdir", "mkdir [-p] dir...", "Create directories", Mkdir));
        add(new Command("rmdir", "rmdir dir...", "Remove empty directories", Rmdir));
        add(new Command("rm", "rm [-rf] path...", "Remove files or directories", Rm));
        add(new Command("cp", "cp [-r] source... dest", "Copy files or directories", Cp));
        add(new Command("mv", "mv source... dest", "Move or rename files", Mv));
        add(new Command("find", "find [path] [-name pattern] [-type f|d]", "Search for files", Find));
        add(new Command("tree", "tree [path]", "Show a directory tree", Tree));
        add(new Command("stat", "stat path...", "Show file details", Stat));
    }

    private static int Ls(CommandContext c)
    {
        if (!c.TryParse("la1", out var flags, out var operands))
        {
            return 2;
        }

        if (operands.Count == 0)
        {
            operands.Add(".");
        }

        int status = 0;
        for (int i = 0; i < operands.Count; i++)
        {
            string path = c.Resolve(operands[i]);
            if (File.Exists(path))
            {
                PrintEntries(c, new List<string> { path }, flags, singleFile: operands[i]);
                continue;
            }

            if (!Directory.Exists(path))
            {
                status = c.Fail("cannot access '" + operands[i] + "': No such file or directory");
                continue;
            }

            if (operands.Count > 1)
            {
                c.WriteLine((i > 0 ? "\n" : "") + operands[i] + ":");
            }

            var entries = new List<string>(Directory.GetFileSystemEntries(path));
            entries.Sort(StringComparer.OrdinalIgnoreCase);
            if (!flags.Contains('a'))
            {
                entries.RemoveAll(e => Path.GetFileName(e).StartsWith('.'));
            }

            PrintEntries(c, entries, flags, singleFile: null);
        }

        return status;
    }

    private static void PrintEntries(CommandContext c, List<string> entries, HashSet<char> flags, string? singleFile)
    {
        if (flags.Contains('l'))
        {
            foreach (string entry in entries)
            {
                bool isDir = Directory.Exists(entry);
                string name = singleFile ?? Path.GetFileName(entry);
                long size = isDir ? 0 : new FileInfo(entry).Length;
                DateTime time = isDir ? Directory.GetLastWriteTime(entry) : File.GetLastWriteTime(entry);
                c.WriteLine((isDir ? "drwxr-xr-x" : "-rw-r--r--") + " user users " + size.ToString().PadLeft(9) + " "
                    + FormatTime(time) + " " + Colorize(name, isDir));
            }

            return;
        }

        if (flags.Contains('1'))
        {
            foreach (string entry in entries)
            {
                c.WriteLine(Colorize(singleFile ?? Path.GetFileName(entry), Directory.Exists(entry)));
            }

            return;
        }

        // Columns sized to the longest name, filling the terminal width.
        int width = int.TryParse(c.Shell.Get("COLUMNS"), out int cols) ? cols : 80;
        int longest = 1;
        foreach (string entry in entries)
        {
            longest = Math.Max(longest, Path.GetFileName(entry).Length + 2);
        }

        int perRow = Math.Max(1, width / longest);
        var line = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            string name = singleFile ?? Path.GetFileName(entries[i]);
            bool lastInRow = (i + 1) % perRow == 0 || i == entries.Count - 1;
            line.Append(Colorize(name, Directory.Exists(entries[i])));
            if (!lastInRow)
            {
                line.Append(new string(' ', longest - name.Length));
            }
            else
            {
                c.WriteLine(line.ToString());
                line.Clear();
            }
        }
    }

    private static string Colorize(string name, bool isDirectory) => isDirectory ? Ansi.Blue(name) : name;

    private static string FormatTime(DateTime t)
        => t.Year + "-" + Pad2(t.Month) + "-" + Pad2(t.Day) + " " + Pad2(t.Hour) + ":" + Pad2(t.Minute);

    private static string Pad2(int v) => v < 10 ? "0" + v : v.ToString();

    private static int Touch(CommandContext c)
    {
        if (c.Args.Length == 0)
        {
            return c.Fail("missing file operand");
        }

        foreach (string arg in c.Args)
        {
            string path = c.Resolve(arg);
            if (File.Exists(path) || Directory.Exists(path))
            {
                File.SetLastWriteTime(path, DateTime.Now);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }
        }

        return 0;
    }

    private static int Mkdir(CommandContext c)
    {
        if (!c.TryParse("p", out var flags, out var operands))
        {
            return 2;
        }

        int status = 0;
        foreach (string arg in operands)
        {
            string path = c.Resolve(arg);
            if (Directory.Exists(path) && !flags.Contains('p'))
            {
                status = c.Fail("cannot create directory '" + arg + "': File exists");
            }
            else if (!flags.Contains('p') && !Directory.Exists(Path.GetDirectoryName(path) ?? "/"))
            {
                status = c.Fail("cannot create directory '" + arg + "': No such file or directory");
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }

        return status;
    }

    private static int Rmdir(CommandContext c)
    {
        int status = 0;
        foreach (string arg in c.Args)
        {
            string path = c.Resolve(arg);
            if (!Directory.Exists(path))
            {
                status = c.Fail("failed to remove '" + arg + "': No such directory");
            }
            else if (Directory.GetFileSystemEntries(path).Length > 0)
            {
                status = c.Fail("failed to remove '" + arg + "': Directory not empty");
            }
            else
            {
                Directory.Delete(path);
            }
        }

        return status;
    }

    private static int Rm(CommandContext c)
    {
        if (!c.TryParse("rf", out var flags, out var operands))
        {
            return 2;
        }

        int status = 0;
        foreach (string arg in operands)
        {
            string path = c.Resolve(arg);
            if (path == "/")
            {
                status = c.Fail("refusing to remove '/'");
            }
            else if (Directory.Exists(path))
            {
                if (!flags.Contains('r'))
                {
                    status = c.Fail("cannot remove '" + arg + "': Is a directory");
                }
                else
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (!flags.Contains('f'))
            {
                status = c.Fail("cannot remove '" + arg + "': No such file or directory");
            }
        }

        return status;
    }

    private static int Cp(CommandContext c)
    {
        if (!c.TryParse("r", out var flags, out var operands))
        {
            return 2;
        }

        return CopyOrMove(c, operands, move: false, recursive: flags.Contains('r'));
    }

    private static int Mv(CommandContext c) => CopyOrMove(c, new List<string>(c.Args), move: true, recursive: true);

    private static int CopyOrMove(CommandContext c, List<string> operands, bool move, bool recursive)
    {
        if (operands.Count < 2)
        {
            return c.Fail("missing destination operand");
        }

        string dest = c.Resolve(operands[^1]);
        bool destIsDir = Directory.Exists(dest);
        if (operands.Count > 2 && !destIsDir)
        {
            return c.Fail("target '" + operands[^1] + "' is not a directory");
        }

        int status = 0;
        for (int i = 0; i < operands.Count - 1; i++)
        {
            string source = c.Resolve(operands[i]);
            string target = destIsDir ? dest + "/" + Path.GetFileName(source) : dest;

            if (File.Exists(source))
            {
                if (move)
                {
                    File.Move(source, target, overwrite: true);
                }
                else
                {
                    File.Copy(source, target, overwrite: true);
                }
            }
            else if (Directory.Exists(source))
            {
                if (!recursive)
                {
                    status = c.Fail("-r not specified; omitting directory '" + operands[i] + "'");
                    continue;
                }

                if (target == source || target.StartsWith(source + "/"))
                {
                    status = c.Fail("cannot copy '" + operands[i] + "' into itself");
                    continue;
                }

                CopyDirectory(source, target);
                if (move)
                {
                    Directory.Delete(source, recursive: true);
                }
            }
            else
            {
                status = c.Fail("cannot stat '" + operands[i] + "': No such file or directory");
            }
        }

        return status;
    }

    /// <summary>Recursively copies a directory tree. Also used by the installer.</summary>
    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, target + "/" + Path.GetFileName(file), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, target + "/" + Path.GetFileName(dir));
        }
    }

    private static int Find(CommandContext c)
    {
        string start = ".";
        string? pattern = null;
        char type = '\0';
        for (int i = 0; i < c.Args.Length; i++)
        {
            string arg = c.Args[i];
            if (arg == "-name" && i + 1 < c.Args.Length)
            {
                pattern = c.Args[++i];
            }
            else if (arg == "-type" && i + 1 < c.Args.Length)
            {
                type = c.Args[++i][0];
            }
            else if (!arg.StartsWith('-'))
            {
                start = arg;
            }
            else
            {
                return c.Fail("unknown predicate '" + arg + "'");
            }
        }

        string root = c.Resolve(start);
        if (!Directory.Exists(root))
        {
            return c.Fail("'" + start + "': No such file or directory");
        }

        // Print paths the way they were asked for: relative stays relative.
        string shown = start.TrimEnd('/');
        if (shown.Length == 0)
        {
            shown = "/";
        }

        FindIn(c, root, shown, pattern, type, isStart: true);
        return 0;
    }

    private static void FindIn(CommandContext c, string path, string shown, string? pattern, char type, bool isStart)
    {
        bool isDir = Directory.Exists(path);
        bool typeOk = type == '\0' || (type == 'd' && isDir) || (type == 'f' && !isDir);
        bool nameOk = pattern is null || Parser.GlobMatch(pattern, Path.GetFileName(path));
        if (typeOk && (nameOk || (isStart && pattern is null)))
        {
            c.WriteLine(shown);
        }

        if (!isDir)
        {
            return;
        }

        var entries = new List<string>(Directory.GetFileSystemEntries(path));
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            FindIn(c, entry, shown == "/" ? "/" + name : shown + "/" + name, pattern, type, isStart: false);
        }
    }

    private static int Tree(CommandContext c)
    {
        string start = c.Args.Length > 0 ? c.Args[0] : ".";
        string root = c.Resolve(start);
        if (!Directory.Exists(root))
        {
            return c.Fail(start + ": No such directory");
        }

        c.WriteLine(Ansi.Blue(start));
        int dirs = 0, files = 0;
        TreeIn(c, root, "", ref dirs, ref files);
        c.WriteLine();
        c.WriteLine(dirs + " directories, " + files + " files");
        return 0;
    }

    private static void TreeIn(CommandContext c, string path, string indent, ref int dirs, ref int files)
    {
        var entries = new List<string>(Directory.GetFileSystemEntries(path));
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            bool last = i == entries.Count - 1;
            bool isDir = Directory.Exists(entries[i]);
            c.WriteLine(indent + (last ? "└── " : "├── ") + Colorize(Path.GetFileName(entries[i]), isDir));
            if (isDir)
            {
                dirs++;
                TreeIn(c, entries[i], indent + (last ? "    " : "│   "), ref dirs, ref files);
            }
            else
            {
                files++;
            }
        }
    }

    private static int Stat(CommandContext c)
    {
        int status = 0;
        foreach (string arg in c.Args)
        {
            string path = c.Resolve(arg);
            if (Directory.Exists(path))
            {
                c.WriteLine("  File: " + path);
                c.WriteLine("  Type: directory");
                c.WriteLine("Modify: " + FormatTime(Directory.GetLastWriteTime(path)));
            }
            else if (File.Exists(path))
            {
                c.WriteLine("  File: " + path);
                c.WriteLine("  Type: regular file");
                c.WriteLine("  Size: " + new FileInfo(path).Length + " bytes");
                c.WriteLine("Modify: " + FormatTime(File.GetLastWriteTime(path)));
            }
            else
            {
                status = c.Fail("cannot stat '" + arg + "': No such file or directory");
            }
        }

        return status;
    }
}
