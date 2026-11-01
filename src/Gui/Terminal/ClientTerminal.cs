using System;
using System.Collections.Generic;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Apps;
using Zenith.Core.Processes;
using Zenith.Core.Security;

namespace Zenith.Gui.Terminal;

/// <summary>A <see cref="TerminalSession"/> for a native client. The session belongs to the desktop thread, which calls
/// <see cref="Tick"/>; the client's service calls only touch the queues here, under the lock.</summary>
internal sealed class ClientTerminal : IProcessTerminal
{
    private const int MaximumOutput = 262144, MaximumKeys = 256;
    private static readonly ConsoleKeyEx[] Letters =
    {
        ConsoleKeyEx.A, ConsoleKeyEx.B, ConsoleKeyEx.C, ConsoleKeyEx.D, ConsoleKeyEx.E, ConsoleKeyEx.F, ConsoleKeyEx.G, ConsoleKeyEx.H, ConsoleKeyEx.I,
        ConsoleKeyEx.J, ConsoleKeyEx.K, ConsoleKeyEx.L, ConsoleKeyEx.M, ConsoleKeyEx.N, ConsoleKeyEx.O, ConsoleKeyEx.P, ConsoleKeyEx.Q, ConsoleKeyEx.R,
        ConsoleKeyEx.S, ConsoleKeyEx.T, ConsoleKeyEx.U, ConsoleKeyEx.V, ConsoleKeyEx.W, ConsoleKeyEx.X, ConsoleKeyEx.Y, ConsoleKeyEx.Z
    };
    private readonly object _gate = new();
    private readonly TerminalSession _session;
    private readonly Queue<KeyEvent> _keys = new();
    private readonly Queue<byte> _output = new();
    private readonly List<KeyEvent> _taken = new();
    private byte[] _state = new byte[24];
    private int _columns;
    private bool _dropped, _closed, _paste;
    private string? _copy;

    public ClientTerminal(Credentials identity, int columns, Func<string, string?, bool> openApp)
    {
        _columns = columns;
        _session = new TerminalSession(identity, columns, openApp, Emit);
    }

    public bool Closed { get { lock (_gate) { return _closed; } } }
    /// <summary>Whether a logout would interrupt something.</summary>
    public bool Busy => _session.IsBusy || _session.HasBackgroundJobs;

    private void Emit(string text)
    {
        lock (_gate)
        {
            foreach (byte value in Encoding.UTF8.GetBytes(text))
            {
                // A client that stopped reading loses the oldest output, not the machine's memory.
                if (_output.Count == MaximumOutput) { _output.Dequeue(); _dropped = true; }
                _output.Enqueue(value);
            }
        }
    }

    public void Key(int code, int modifiers, int extra)
    {
        lock (_gate)
        {
            if (!_closed && _keys.Count < MaximumKeys) { _keys.Enqueue(ToKey(code, modifiers, extra)); }
        }
    }

    public int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            int count = 0;
            while (count < destination.Length && _output.Count > 0) { destination[count++] = _output.Dequeue(); }
            return count;
        }
    }

    public byte[] State() { lock (_gate) { return _state; } }
    public void Resize(int columns) { lock (_gate) { _columns = columns; } }
    public void Copy(string text) { lock (_gate) { _copy = text; } }
    public void Paste() { lock (_gate) { _paste = true; } }
    public void Close() { lock (_gate) { _closed = true; _keys.Clear(); } }

    /// <summary>On the desktop thread: applies keys, runs the session and publishes its state.</summary>
    public void Tick()
    {
        int columns;
        bool closed, paste;
        string? copy;
        lock (_gate)
        {
            closed = _closed; columns = _columns; paste = _paste; copy = _copy; _paste = false; _copy = null;
            _taken.Clear();
            while (_keys.Count > 0) { _taken.Add(_keys.Dequeue()); }
        }
        if (closed) { _session.Cancel(); return; }
        using var identity = CredentialContext.Enter(_session.Identity);
        _session.SyncNativeInput();
        foreach (KeyEvent key in _taken)
        {
            if (_session.RawActive) { _session.KeyRaw(key); }
            else if (!(_session.SecretRead && key.Key is ConsoleKeyEx.Tab or ConsoleKeyEx.UpArrow or ConsoleKeyEx.DownArrow)) { _session.Key(key); }
        }
        // Never into or out of a hidden prompt.
        if (copy is not null && !_session.SecretRead) { Clipboard.Text = copy; }
        if (paste && !_session.SecretRead && !_session.RawActive) { _session.Paste(Clipboard.Text); }
        _session.Sync(columns);
        bool dropped;
        lock (_gate) { dropped = _dropped; _dropped = false; }
        byte[] state = ProcessTerminalState.Encode(_session.IsBusy, _session.SecretRead, _session.RawActive, _session.ExitRequested, dropped,
            _session.Cursor, _session.DisplayPrompt, _session.SecretRead ? string.Empty : _session.InputText, _session.WorkingDirectory);
        lock (_gate) { _state = state; }
    }

    /// <summary>The same key the desktop would have made, from what a client received in a key event.</summary>
    private static KeyEvent ToKey(int code, int modifiers, int extra)
    {
        ConsoleKeyEx key = extra switch
        {
            1 => ConsoleKeyEx.Escape, 2 => ConsoleKeyEx.Enter, 3 => ConsoleKeyEx.Backspace, 4 => ConsoleKeyEx.Tab,
            5 => ConsoleKeyEx.LeftArrow, 6 => ConsoleKeyEx.RightArrow, 7 => ConsoleKeyEx.UpArrow, 8 => ConsoleKeyEx.DownArrow,
            9 => ConsoleKeyEx.Delete, 10 => ConsoleKeyEx.Home, 11 => ConsoleKeyEx.End, 12 => ConsoleKeyEx.PageUp, 13 => ConsoleKeyEx.PageDown,
            _ => code is >= 'a' and <= 'z' ? Letters[code - 'a'] : code is >= 'A' and <= 'Z' ? Letters[code - 'A'] : ConsoleKeyEx.NoName
        };
        bool control = (modifiers & 2) != 0;
        char text = extra == 2 ? '\n' : extra != 0 || (control && (modifiers & 4) == 0) || code is < 0 or > 0xffff ? '\0' : (char)code;
        return new KeyEvent(text, key, (modifiers & 1) != 0, (modifiers & 4) != 0, control, KeyEvent.KeyEventType.Make);
    }
}
