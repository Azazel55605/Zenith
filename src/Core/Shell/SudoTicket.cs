using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Zenith.Core.Shell;

/// <summary>sudo's remembered authentication for one terminal: after a correct password, the same
/// account runs further sudo commands without one for five minutes. Changing account (su, login)
/// or <c>sudo -k</c> forgets it. Never shared between terminals.</summary>
internal sealed class SudoTicket
{
    public const long LifetimeMilliseconds = 5 * 60 * 1000;
    private readonly object _gate = new();
    private uint _uid;
    private long _until;
    private readonly Func<long> _now;
    public SudoTicket() : this(() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency) { }
    public SudoTicket(Func<long> now) => _now = now;
    public bool Valid(uint uid) { lock (_gate) { return _until != 0 && _uid == uid && _now() < _until; } }
    public void Grant(uint uid) { lock (_gate) { _uid = uid; _until = _now() + LifetimeMilliseconds; } }
    public void Forget() { lock (_gate) { _until = 0; } }
}

/// <summary>What a sudo command line asks for. Options end at the first word that is not one, or at --.</summary>
internal sealed class SudoRequest
{
    public string User = "root";
    public bool NonInteractive, Validate, Forget, ForgetOnly, List;
    public List<string> Command = new();

    public const string Usage = "sudo [-n] [-u user] command [args...] | sudo -v | sudo -k | sudo -l";

    /// <summary>Parses the arguments; null and an error message when they are invalid.</summary>
    public static SudoRequest? Parse(IReadOnlyList<string> args, out string? error)
    {
        var request = new SudoRequest(); error = null; int i = 0;
        for (; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') { break; }
            if (a == "-u" || a == "--user")
            {
                if (++i == args.Count) { error = "option -u needs a user name"; return null; }
                request.User = args[i]; continue;
            }
            if (a.StartsWith("--user=", StringComparison.Ordinal)) { request.User = a[7..]; continue; }
            if (a == "--non-interactive") { request.NonInteractive = true; continue; }
            if (a == "--validate") { request.Validate = true; continue; }
            if (a == "--reset-timestamp") { request.Forget = true; continue; }
            if (a == "--list") { request.List = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) { error = "unknown option " + a; return null; }
            for (int c = 1; c < a.Length; c++)
            {
                switch (a[c])
                {
                    case 'n': request.NonInteractive = true; break;
                    case 'v': request.Validate = true; break;
                    case 'k': request.Forget = true; break;
                    case 'l': request.List = true; break;
                    case 'u':
                        if (c != a.Length - 1) { request.User = a[(c + 1)..]; c = a.Length; break; }
                        if (++i == args.Count) { error = "option -u needs a user name"; return null; }
                        request.User = args[i]; break;
                    default: error = "unknown option -" + a[c]; return null;
                }
            }
        }
        for (; i < args.Count; i++) { request.Command.Add(args[i]); }
        int modes = (request.Validate ? 1 : 0) + (request.List ? 1 : 0);
        if (modes > 1 || ((request.Validate || request.List) && request.Command.Count > 0))
        { error = "-v and -l take no command; see help sudo"; return null; }
        request.ForgetOnly = request.Forget && request.Command.Count == 0 && !request.Validate && !request.List;
        if (request.Command.Count == 0 && !request.Validate && !request.List && !request.ForgetOnly)
        { error = "no command given; usage: " + Usage; return null; }
        if (request.User.Length == 0) { error = "empty user name"; return null; }
        return request;
    }
}
