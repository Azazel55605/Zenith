using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Zenith.Core.Security;
using Zenith.Core.Shell;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Core.Console;

/// <summary>Bounded ASCII serial line discipline and one cancellable shell worker.</summary>
internal sealed class SerialTerminal : IOutput
{
    public const int MaximumLine = 4096;
    public const int MaximumScript = 16384;
    public const int MaximumOutput = 65536;
    public const int MaximumQueuedLines = 8;
    public const int MaximumHistory = 64;
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _script = new();
    private readonly Queue<string> _queued = new();
    private readonly Func<IOutput, CommandShell> _factory;
    private readonly Func<bool>? _refresh;
    private readonly Action? _endSession;
    private CommandShell _shell = null!;
    private volatile bool _busy;
    private bool _completed, _exit, _reading, _secret, _answered;
    private string? _answer;
    private bool _overflow, _escape, _afterCr;
    private bool _closing;
    private int _history = -1;
    private string _draft = "";
    public bool Busy => _busy;
    public CommandShell Shell => _shell;
    public void CloseInput()
    {
        lock (_gate) { _closing = true; _queued.Clear(); _line.Clear(); _script.Clear(); _draft = ""; }
        _shell.Cancel();
    }

    public SerialTerminal(Func<IOutput, CommandShell> factory, Func<bool>? refresh = null, Action? endSession = null, string banner = "COM1 ASCII console")
    {
        _factory = factory; _refresh = refresh; _endSession = endSession;
        ResetShell();
        Emit("\r\n[console] " + banner + "; Ctrl+C cancels, Ctrl+D ends input.\r\n");
        Prompt();
    }
    private void ResetShell()
    {
        _shell = _factory(this);
        _shell.ReadInputLine = prompt => ReadLine(prompt, false);
        _shell.ReadSecret = prompt => ReadLine(prompt, true);
        _shell.ExitRequested += () => { lock (_gate) { _exit = true; } };
    }
    // Worker writes wait for the main loop to drain, never retaining unbounded output.
    public void Write(string text)
    {
        int offset = 0;
        while (offset < text.Length)
        {
            _shell?.ThrowIfCancelled();
            lock (_gate)
            {
                int count = Math.Min(text.Length - offset, MaximumOutput - _output.Length);
                if (count > 0) { _output.Append(text, offset, count); offset += count; }
            }
            if (offset < text.Length) { Thread.Sleep(10); }
        }
    }
    private void Emit(string text)
    {
        // Main-loop echoes must not wait on their own output drain.
        lock (_gate)
        {
            if (text.Length <= MaximumOutput - _output.Length) { _output.Append(text); }
        }
    }
    public string Drain(int maximum = 1024)
    {
        lock (_gate)
        {
            int count = Math.Min(Math.Max(maximum, 0), _output.Length);
            string text = _output.ToString(0, count);
            _output.Remove(0, count);
            return text;
        }
    }
    private void Prompt() => Emit("\r\n[console] " + (_script.Length > 0 ? _shell.ContinuationPrompt : _shell.Prompt));
    public void Tick()
    {
        lock (_gate)
        {
            if (!_completed) { return; }
            _completed = false;
            if (_closing) { _output.Clear(); return; }
            if (_shell.History.Count > MaximumHistory)
            { _shell.History.RemoveRange(0, _shell.History.Count - MaximumHistory); }
            bool exit = _exit;
            if (exit) { _endSession?.Invoke(); }
            if (exit || _refresh?.Invoke() == true)
            {
                _exit = false; _queued.Clear(); _line.Clear(); _script.Clear();
                ResetShell(); Emit("\r\n[console] session " + (exit ? "ended" : "changed") + "\r\n");
            }
            Prompt();
            if (_queued.Count > 0) { Accept(_queued.Dequeue()); }
        }
    }
    public void Receive(byte value)
    {
        if (value < 128) { ReceiveCharacter((char)value); }
    }
    public void Recall(bool older)
    {
        lock (_gate)
        {
            if (_busy || _reading || _completed || _script.Length > 0 || _shell.History.Count == 0) { return; }
            if (_history < 0) { if (!older) { return; } _draft = _line.ToString(); _history = _shell.History.Count; }
            int next = Math.Clamp(_history + (older ? -1 : 1), 0, _shell.History.Count);
            Emit(new string('\b', _line.Length) + new string(' ', _line.Length) + new string('\b', _line.Length));
            _line.Clear(); _line.Append(next == _shell.History.Count ? _draft : _shell.History[next]); Emit(_line.ToString());
            _history = next;
        }
    }
    public void ReceiveCharacter(char value)
    {
        if (_closing) { return; }
        // CRLF is one submission. Ignore ANSI escape sequences rather than execute them.
        if (value == 10 && _afterCr) { _afterCr = false; return; }
        _afterCr = value == 13;
        if (value == 3)
        {
            if (_busy) { _shell.Cancel(); }
            lock (_gate) { _queued.Clear(); _script.Clear(); _line.Clear(); _overflow = false; _escape = false; }
            Emit("^C\r\n"); if (!_busy) { Prompt(); } return;
        }
        lock (_gate)
        {
            if (value == 27) { _escape = true; return; }
            if (_escape) { if (value >= 64 && value <= 126 && value != '[' && value != 'O') { _escape = false; } return; }
            if (value == 21) { _line.Clear(); _overflow = false; if (!_secret) { Emit("\r\n"); } return; }
            if (value == 8 || value == 127)
            {
                if (_line.Length > 0) { _line.Length--; if (!_secret) { Emit("\b \b"); } }
                return;
            }
            if (value == 4 && _line.Length == 0)
            {
                if (_reading) { _answered = true; _answer = null; }
                else if (!_busy && !_completed && _script.Length == 0) { Accept("exit"); }
                return;
            }
            if (value == 10 || value == 13)
            {
                string line = _line.ToString(); _line.Clear(); Emit("\r\n");
                _history = -1; _draft = "";
                if (_overflow)
                {
                    _overflow = false;
                    Emit("[console] line too long; discarded\r\n");
                    if (_reading) { _answered = true; _answer = null; }
                    else if (!_busy) { _script.Clear(); Prompt(); }
                    return;
                }
                if (_reading)
                {
                    if (!_answered) { _answer = line; _answered = true; }
                }
                else if (_busy || _completed)
                {
                    if (_queued.Count < MaximumQueuedLines) { _queued.Enqueue(line); }
                    else { Emit("[console] command queue full; discarded\r\n"); }
                }
                else { Accept(line); }
                return;
            }
            if (value >= 32 && !char.IsControl(value))
            {
                if (_line.Length == MaximumLine) { _overflow = true; return; }
                _line.Append((char)value);
                if (!_secret) { Emit(((char)value).ToString()); }
            }
        }
    }
    private void Accept(string line)
    {
        string text = _script.Length > 0 ? _script + "\n" + line : line;
        _script.Clear();
        if (text.Length > MaximumScript) { Emit("[console] script too long; discarded\r\n"); Prompt(); return; }
        if (!CommandShell.IsComplete(text)) { _script.Append(text); Prompt(); return; }
        if (text.Trim().Length == 0) { Prompt(); return; }
        _busy = true;
        new Thread(() =>
        {
            using var credentials = CredentialContext.Enter(_shell.Identity);
            try { _shell.Execute(text); }
            catch (Exception e) { Emit("[console] command failed: " + e.Message + "\r\n"); }
            finally { lock (_gate) { _completed = true; _busy = false; } }
        }).Start();
    }
    private string? ReadLine(string prompt, bool secret)
    {
        lock (_gate)
        {
            _reading = true; _secret = secret; _answered = false; _answer = null;
            _line.Clear(); _overflow = false;
            if (secret) { _queued.Clear(); }
            else if (_queued.Count > 0) { _answer = _queued.Dequeue(); _answered = true; }
        }
        try
        {
            Write(prompt);
            while (!_shell.IsCancelled)
            {
                lock (_gate) { if (_answered) { return _answer; } }
                Thread.Sleep(20);
            }
            return null;
        }
        finally
        {
            lock (_gate) { _reading = false; _secret = false; _answer = null; _line.Clear(); _overflow = false; }
        }
    }
}
