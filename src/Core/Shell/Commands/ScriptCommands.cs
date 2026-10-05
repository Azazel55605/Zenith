using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Zenith.Core.Shell.Commands;

/// <summary>Built-ins for scripting: tests, input, control flow, and running scripts.</summary>
internal static class ScriptCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("test", "test expression", "Evaluate a condition (see 'help [')", Test));
        add(new Command("[", "[ expression ]", "Evaluate a condition: -e -f -d -s -z -n file/string tests, = != -eq -ne -lt -le -gt -ge, ! -a -o ( )", Bracket));
        add(new Command("read", "read [-r] [-p prompt] [name...]", "Read a line into variables (REPLY by default)", Read));
        add(new Command("sh", "sh [-c command [args...] | script [args...]]", "Run a script or command string in a child shell", Sh));
        add(new Command("source", "source file [args...]", "Run a script in the current shell", Source));
        add(new Command(".", ". file [args...]", "Run a script in the current shell", Source));
        add(new Command("shift", "shift [n]", "Drop the first n positional parameters", Shift));
        add(new Command("break", "break [n]", "Leave the innermost (or n-th) enclosing loop", c => c.Shell.RequestBreak(Count(c, 1), isContinue: false)));
        add(new Command("continue", "continue [n]", "Start the next iteration of the innermost (or n-th) loop", c => c.Shell.RequestBreak(Count(c, 1), isContinue: true)));
        add(new Command("return", "return [status]", "Return from a function or sourced script", c => c.Shell.RequestReturn(Count(c, c.Shell.LastStatus))));
        add(new Command("exit", "exit [status]", "Leave the script, or close the terminal at the prompt", c => c.Shell.RequestScriptExit(Count(c, c.Shell.LastStatus))));
        add(new Command("type", "type name...", "Tell how a name would be run", Type));
    }

    private static int Count(CommandContext c, int fallback)
        => c.Args.Length > 0 && int.TryParse(c.Args[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static int Test(CommandContext c) => Evaluate(c, new List<string>(c.Args));

    private static int Bracket(CommandContext c)
    {
        if (c.Args.Length == 0 || c.Args[^1] != "]")
        {
            c.Fail("missing ']'");
            return 2;
        }

        return Evaluate(c, new List<string>(c.Args[..^1]));
    }

    private static int Evaluate(CommandContext c, List<string> args)
    {
        if (args.Count == 0)
        {
            return 1;
        }

        var evaluator = new TestEvaluator(args, c.Resolve);
        bool? result = evaluator.Run(out string? error);
        if (result is null)
        {
            c.Fail(error ?? "syntax error");
            return 2;
        }

        return result.Value ? 0 : 1;
    }

    private static int Read(CommandContext c)
    {
        string prompt = string.Empty;
        var names = new List<string>();
        for (int i = 0; i < c.Args.Length; i++)
        {
            if (c.Args[i] == "-p" && i + 1 < c.Args.Length)
            {
                prompt = c.Args[++i];
            }
            else if (c.Args[i] != "-r")
            {
                names.Add(c.Args[i]);
            }
        }

        if (names.Count == 0)
        {
            names.Add("REPLY");
        }

        string? line = c.Input is not null ? c.Input.ReadLine() : c.Shell.ReadInputLine?.Invoke(prompt);
        if (line is null)
        {
            return 1;   // end of input
        }

        // Split on whitespace; the last variable takes the rest of the line.
        string rest = line.Trim();
        for (int i = 0; i < names.Count; i++)
        {
            string value;
            if (i == names.Count - 1)
            {
                value = rest;
            }
            else
            {
                int space = rest.IndexOfAny(new[] { ' ', '\t' });
                value = space < 0 ? rest : rest.Substring(0, space);
                rest = space < 0 ? string.Empty : rest.Substring(space + 1).TrimStart();
            }

            c.Shell.Set(names[i], value);
        }

        return 0;
    }

    private static int Sh(CommandContext c)
    {
        if (c.Args.Length >= 2 && c.Args[0] == "-c")
        {
            var args = new List<string>(c.Args[2..]);
            return c.Shell.RunCommandString(c.Args[1], args, null, c.Out);
        }

        if (c.Args.Length >= 1)
        {
            string path = c.Resolve(c.Args[0]);
            if (!File.Exists(path))
            {
                return c.Fail(c.Args[0] + ": No such file or directory");
            }

            return c.Shell.RunScript(path, new List<string>(c.Args[1..]), c.Input, c.Out);
        }

        if (c.Input is not null)
        {
            return c.Shell.RunCommandString(c.Stdin ?? string.Empty, new List<string>(), null, c.Out);   // cat script | sh
        }

        return c.Fail("usage: sh [-c command | script] (an interactive sub-shell is not supported)");
    }

    private static int Source(CommandContext c)
    {
        if (c.Args.Length == 0)
        {
            return c.Fail("usage: " + c.Name + " file [args...]");
        }

        string path = c.Resolve(c.Args[0]);
        if (!File.Exists(path) && !c.Args[0].Contains('/'))
        {
            foreach (string dir in c.Shell.Get("PATH").Split(':'))
            {
                string candidate = c.Resolve(dir) + "/" + c.Args[0];
                if (dir.Length > 0 && File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }
        }

        if (!File.Exists(path))
        {
            return c.Fail(c.Args[0] + ": No such file or directory");
        }

        return c.Shell.Source(path, new List<string>(c.Args[1..]), c.Input, c.Out);
    }

    private static int Shift(CommandContext c)
    {
        int n = Count(c, 1);
        return n >= 0 && c.Shell.Shift(n) ? 0 : c.Fail("shift count out of range");
    }

    private static int Type(CommandContext c)
    {
        int status = 0;
        foreach (string name in c.Args)
        {
            if (c.Shell.HasFunction(name))
            {
                c.WriteLine(name + " is a shell function");
            }
            else if (Shell.Commands.ContainsKey(name))
            {
                c.WriteLine(name + " is a shell built-in");
            }
            else
            {
                string? path = null;
                foreach (string dir in c.Shell.Get("PATH").Split(':'))
                {
                    string candidate = c.Resolve(dir) + "/" + name;
                    if (dir.Length > 0 && File.Exists(candidate))
                    {
                        path = candidate;
                        break;
                    }
                }

                if (path is not null)
                {
                    c.WriteLine(name + " is " + path);
                }
                else
                {
                    status = c.Fail(name + ": not found");
                }
            }
        }

        return status;
    }

    /// <summary>The <c>test</c> expression grammar: or := and ('-o' and)*, and := not ('-a' not)*, not := '!' not | primary.</summary>
    private sealed class TestEvaluator
    {
        private static readonly HashSet<string> s_unary = new() { "-e", "-f", "-d", "-s", "-z", "-n", "-r", "-w", "-x" };
        private static readonly HashSet<string> s_binary = new() { "=", "==", "!=", "-eq", "-ne", "-lt", "-le", "-gt", "-ge" };

        private readonly List<string> _args;
        private readonly Func<string, string> _resolve;
        private int _pos;
        private string? _error;

        public TestEvaluator(List<string> args, Func<string, string> resolve)
        {
            _args = args;
            _resolve = resolve;
        }

        public bool? Run(out string? error)
        {
            bool result = Or();
            if (_error is null && _pos < _args.Count)
            {
                _error = "unexpected '" + _args[_pos] + "'";
            }

            error = _error;
            return _error is null ? result : null;
        }

        private bool Or()
        {
            bool left = And();
            while (_pos < _args.Count && _args[_pos] == "-o")
            {
                _pos++;
                bool right = And();
                left = left || right;
            }

            return left;
        }

        private bool And()
        {
            bool left = Not();
            while (_pos < _args.Count && _args[_pos] == "-a")
            {
                _pos++;
                bool right = Not();
                left = left && right;
            }

            return left;
        }

        private bool Not()
        {
            if (_pos < _args.Count && _args[_pos] == "!" && _pos + 1 < _args.Count)
            {
                _pos++;
                return !Not();
            }

            return Primary();
        }

        private bool Primary()
        {
            if (_pos >= _args.Count)
            {
                _error ??= "argument expected";
                return false;
            }

            string arg = _args[_pos];
            if (arg == "(")
            {
                _pos++;
                bool inner = Or();
                if (_pos >= _args.Count || _args[_pos] != ")")
                {
                    _error ??= "missing ')'";
                    return false;
                }

                _pos++;
                return inner;
            }

            // Binary first: in "[ -n = -n ]" the first -n is an operand.
            if (_pos + 2 < _args.Count && s_binary.Contains(_args[_pos + 1]))
            {
                string op = _args[_pos + 1];
                string right = _args[_pos + 2];
                _pos += 3;
                return Binary(arg, op, right);
            }

            if (s_unary.Contains(arg) && _pos + 1 < _args.Count)
            {
                string operand = _args[_pos + 1];
                _pos += 2;
                return Unary(arg, operand);
            }

            _pos++;
            return arg.Length > 0;   // a lone string is true when non-empty
        }

        private bool Unary(string op, string operand)
        {
            switch (op)
            {
                case "-z":
                    return operand.Length == 0;
                case "-n":
                    return operand.Length > 0;
            }

            string path = _resolve(operand);
            return op switch
            {
                "-e" => File.Exists(path) || Directory.Exists(path),
                "-f" => File.Exists(path),
                "-d" => Directory.Exists(path),
                "-s" => File.Exists(path) && new FileInfo(path).Length > 0,
                _ => File.Exists(path) || Directory.Exists(path),   // -r -w -x: no permissions yet
            };
        }

        private bool Binary(string left, string op, string right)
        {
            switch (op)
            {
                case "=":
                case "==":
                    return left == right;
                case "!=":
                    return left != right;
            }

            if (!long.TryParse(left, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long a)
                || !long.TryParse(right, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long b))
            {
                _error ??= "integer expression expected";
                return false;
            }

            return op switch
            {
                "-eq" => a == b,
                "-ne" => a != b,
                "-lt" => a < b,
                "-le" => a <= b,
                "-gt" => a > b,
                _ => a >= b,
            };
        }
    }
}
