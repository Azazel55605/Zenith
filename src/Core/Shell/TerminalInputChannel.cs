using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Zenith.Core.IO;

namespace Zenith.Core.Shell;

/// <summary>One foreground terminal lease; raw bytes never become shell type-ahead.</summary>
internal sealed class TerminalInputChannel(Func<string?> readLine, Action<string> echo, Action interrupt)
{
    public const int Canonical = 1, Echo = 2, Signals = 4, DefaultMode = 7, MaximumBytes = 4096;
    private readonly Func<string?> _readLine = readLine;
    private readonly object _gate = new();
    private readonly Queue<byte> _bytes = new();
    private Lease? _owner;
    private int _mode = DefaultMode, _generation;
    private bool _overflow;
    private Decoder _echoDecoder = Encoding.UTF8.GetDecoder();
    public bool RawActive { get { lock (_gate) { return _owner is not null && (_mode & Canonical) == 0; } } }
    public int Generation { get { lock (_gate) { return _generation; } } }
    public ITerminalInput Acquire(Func<bool> cancelled)
    {
        lock (_gate)
        {
            if (_owner is not null) { throw new IOException("terminal already has a foreground native reader"); }
            return _owner = new Lease(this, cancelled, new LineByteInput(_readLine, cancelled));
        }
    }
    public bool Receive(char value) => Receive(value.ToString());
    public bool Receive(string text) => ReceiveBytes(Encoding.UTF8.GetBytes(text));
    public bool ReceiveBytes(byte[] bytes)
    {
        bool signal = false, display;
        string echoed="";
        lock (_gate)
        {
            if (_owner is null || (_mode & Canonical) != 0) { return false; }
            display = (_mode & Echo) != 0;
            foreach (byte value in bytes)
            {
                if (value == 3 && (_mode & Signals) != 0) { signal = true; _bytes.Clear(); break; }
                if (_bytes.Count == MaximumBytes) { _overflow = true; break; }
                _bytes.Enqueue(value);
            }
            if(display && !signal)
            {
                var chars=new char[bytes.Length+2];
                int count=_echoDecoder.GetChars(bytes,0,bytes.Length,chars,0,false);
                echoed=new string(chars,0,count);
            }
        }
        if (signal) { interrupt(); if (display) { echo("^C\r\n"); } }
        else if (display && echoed.Length>0) { echo(echoed); }
        return true;
    }
    private sealed class Lease(TerminalInputChannel terminal, Func<bool> cancelled, LineByteInput canonical) : ITerminalInput
    {
        private LineByteInput _canonical = canonical;
        public int GetMode()
        { lock (terminal._gate) { return ReferenceEquals(terminal._owner, this) ? terminal._mode : -9; } }
        public int SetMode(int flags)
        {
            if (flags < 0 || flags > DefaultMode || (flags & Canonical) != 0 && flags != DefaultMode) { return -22; }
            lock (terminal._gate)
            {
                if (!ReferenceEquals(terminal._owner, this)) { return -9; }
                if (terminal._mode != flags)
                {
                    terminal._mode = flags; terminal._bytes.Clear(); terminal._overflow = false; terminal._echoDecoder=Encoding.UTF8.GetDecoder();
                    _canonical = new LineByteInput(terminal._readLine, cancelled);
                    terminal._generation++;
                }
                return 0;
            }
        }
        public int Read(byte[] buffer)
        {
            if (buffer.Length == 0) { return 0; }
            while (true)
            {
                if (cancelled()) { return -125; }
                bool cooked;
                lock (terminal._gate)
                {
                    if (!ReferenceEquals(terminal._owner, this)) { return -9; }
                    cooked = (terminal._mode & Canonical) != 0;
                    if (!cooked)
                    {
                        if (terminal._overflow) { return -75; }
                        int count = Math.Min(buffer.Length, terminal._bytes.Count);
                        for (int i = 0; i < count; i++) { buffer[i] = terminal._bytes.Dequeue(); }
                        if (count > 0) { return count; }
                    }
                }
                if (cooked) { return _canonical.Read(buffer); }
                Thread.Sleep(1);
            }
        }
        public void Dispose()
        {
            lock (terminal._gate)
            {
                if (!ReferenceEquals(terminal._owner, this)) { return; }
                if (terminal._mode != DefaultMode) { terminal._generation++; }
                terminal._owner = null; terminal._mode = DefaultMode; terminal._bytes.Clear(); terminal._overflow = false; terminal._echoDecoder=Encoding.UTF8.GetDecoder();
            }
        }
    }
}
