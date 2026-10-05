using System;
using System.Collections.Generic;
using System.Text;

namespace MyOS.Core.Shell.Commands;

/// <summary>Commands that act on the shell itself: navigation, variables, history, help.</summary>
internal static class BuiltinCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("help", "help [command]", "List commands, or show usage of one", Help));
        add(new Command("echo", "echo [-n] [text...]", "Print arguments", Echo));
        add(new Command("cd", "cd [dir]", "Change the working directory", Cd));
        add(new Command("pwd", "pwd", "Print the working directory", c => { c.WriteLine(c.Shell.WorkingDirectory); return 0; }));
        add(new Command("export", "export [NAME=value...]", "Set environment variables, or list them", Export));
        add(new Command("unset", "unset NAME...", "Remove environment variables", Unset));
        add(new Command("env", "env", "Print the environment", Env));
        add(new Command("history", "history", "Show previously entered commands", History));
        add(new Command("clear", "clear", "Clear the terminal", c => { c.Write(Ansi.ClearScreen); return 0; }));
        add(new Command("exit", "exit", "Close this shell", c => { c.Shell.RequestExit(); return 0; }));
        add(new Command("true", "true", "Do nothing, successfully", _ => 0));
        add(new Command("false", "false", "Do nothing, unsuccessfully", _ => 1));
        add(new Command("which", "which command...", "Show where a command comes from", Which));
        add(new Command("open", "open app", "Open a desktop application (welcome, notes, system, terminal)", Open));
    }

    private static int Help(CommandContext c)
    {
        if (c.Args.Length > 0)
        {
            if (!Shell.Commands.TryGetValue(c.Args[0], out Command? command))
            {
                return c.Fail("no such command: " + c.Args[0]);
            }

            c.WriteLine("usage: " + command.Usage);
            c.WriteLine(command.Summary);
            return 0;
        }

        var names = new List<string>(Shell.Commands.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            c.WriteLine(Ansi.Bold(name.PadRight(10)) + " " + Shell.Commands[name].Summary);
        }

        c.WriteLine();
        c.WriteLine(Ansi.Dim("Pipes (|), redirection (> >> <), ; && || and wildcards work. 'help cmd' shows usage."));
        return 0;
    }

    private static int Echo(CommandContext c)
    {
        bool newline = true;
        int start = 0;
        if (c.Args.Length > 0 && c.Args[0] == "-n")
        {
            newline = false;
            start = 1;
        }

        var sb = new StringBuilder();
        for (int i = start; i < c.Args.Length; i++)
        {
            if (i > start)
            {
                sb.Append(' ');
            }

            sb.Append(c.Args[i]);
        }

        c.Write(newline ? sb + "\n" : sb.ToString());
        return 0;
    }

    private static int Cd(CommandContext c)
    {
        string target = c.Args.Length == 0 ? c.Shell.Get("HOME") : c.Args[0];
        if (target == "-")
        {
            target = c.Shell.Get("OLDPWD");
        }

        string previous = c.Shell.WorkingDirectory;
        if (!c.Shell.ChangeDirectory(target))
        {
            return c.Fail(target + ": No such directory");
        }

        c.Shell.Set("OLDPWD", previous);
        return 0;
    }

    private static int Export(CommandContext c)
    {
        if (c.Args.Length == 0)
        {
            return Env(c);
        }

        foreach (string arg in c.Args)
        {
            int eq = arg.IndexOf('=');
            if (eq > 0)
            {
                c.Shell.Set(arg.Substring(0, eq), arg.Substring(eq + 1));
            }
        }

        return 0;
    }

    private static int Unset(CommandContext c)
    {
        foreach (string arg in c.Args)
        {
            c.Shell.Unset(arg);
        }

        return 0;
    }

    private static int Env(CommandContext c)
    {
        var names = new List<string>(c.Shell.Environment.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
        {
            c.WriteLine(name + "=" + c.Shell.Environment[name]);
        }

        return 0;
    }

    private static int History(CommandContext c)
    {
        for (int i = 0; i < c.Shell.History.Count; i++)
        {
            c.WriteLine((i + 1).ToString().PadLeft(5) + "  " + c.Shell.History[i]);
        }

        return 0;
    }

    private static int Which(CommandContext c)
    {
        int status = 0;
        foreach (string name in c.Args)
        {
            if (Shell.Commands.ContainsKey(name))
            {
                c.WriteLine(name + ": shell built-in");
            }
            else
            {
                status = c.Fail(name + ": not found");
            }
        }

        return status;
    }

    private static int Open(CommandContext c)
    {
        if (c.Args.Length != 1)
        {
            return c.Fail("usage: open app");
        }

        if (c.Shell.OpenApp is null)
        {
            return c.Fail("no desktop is running");
        }

        return c.Shell.OpenApp(c.Args[0]) ? 0 : c.Fail(c.Args[0] + ": no such application");
    }
}
