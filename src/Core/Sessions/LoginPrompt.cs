using System;
using System.Threading;
using Zenith.Core.Security;

namespace Zenith.Core.Sessions;

/// <summary>One bounded authentication attempt; only the UI thread activates a result.</summary>
internal sealed class LoginPrompt
{
    private readonly Func<string, string, Credentials?> _authenticate;
    private Credentials? _result;
    private volatile bool _completed;
    public volatile bool Busy;
    public string Message { get; private set; } = "Enter your username and password.";
    public LoginPrompt(Func<string, string, Credentials?> authenticate) => _authenticate = authenticate;
    public void Submit(string name, string password)
    {
        if (Busy) { return; }
        if (!AccountDatabase.ValidName(name) || password.Length > 256) { Message = "Authentication failed."; return; }
        Busy = true; _completed = false; _result = null; Message = "Signing in...";
        new Thread(() =>
        {
            Credentials? identity = null;
            try { identity = _authenticate(name, password); }
            catch { }
            password = string.Empty;
            if (identity is null) { Thread.Sleep(1000); }
            _result = identity; _completed = true;
        }).Start();
    }
    public bool TakeResult(out Credentials? identity)
    {
        identity = null;
        if (!_completed) { return false; }
        _completed = false; Busy = false; identity = _result; _result = null;
        Message = identity is null ? "Authentication failed. Please try again." : "Signed in.";
        return true;
    }
}
