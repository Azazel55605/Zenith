using System;
using System.Collections.Generic;

namespace Zenith.Core.Processes;

internal enum ProcessState { Starting, Running, Waiting, Exited }
internal sealed record ProcessInfo(int Pid, string Name, uint Uid, ProcessState State, int? Status, bool StopRequested);

/// <summary>Bounded process metadata, owner-authorized cooperative stop and exit history.</summary>
internal sealed class ProcessTable
{
    private sealed class Entry(int pid, string name, uint uid, Action stop)
    {
        public readonly int Pid = pid;
        public readonly string Name = name;
        public readonly uint Uid = uid;
        public ProcessState State;
        public int? Status;
        public bool StopRequested;
        public Action? Stop = stop;
        public ProcessInfo Snapshot() => new(Pid, Name, Uid, State, Status, StopRequested);
    }
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly Queue<int> _exited = new();
    private readonly int _maximum, _history;
    private int _next, _active;
    public ProcessTable(int maximum = 8, int history = 32)
    {
        if (maximum is < 1 or > 64 || history is < 0 or > 256) { throw new ArgumentOutOfRangeException(); }
        _maximum = maximum; _history = history;
    }
    public int ActiveCount { get { lock (_gate) { return _active; } } }
    public int Start(string name, uint uid, Action stop)
    {
        if (name.Length is < 1 or > 64) { throw new ArgumentException("invalid process name"); }
        foreach (char value in name)
        { if (!(value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')) { throw new ArgumentException("invalid process name"); } }
        ArgumentNullException.ThrowIfNull(stop);
        lock (_gate)
        {
            if (_active == _maximum || _next == int.MaxValue) { return -1; }
            int pid = ++_next; _entries.Add(pid, new Entry(pid, name, uid, stop)); _active++; return pid;
        }
    }
    public void SetState(int pid, ProcessState state)
    {
        if (state == ProcessState.Exited) { throw new ArgumentException("use Complete for exit"); }
        lock (_gate)
        {
            if (_entries.TryGetValue(pid, out var entry) && entry.State != ProcessState.Exited)
            { entry.State = state; }
        }
    }
    public void Complete(int pid, int status)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pid, out var entry) || entry.State == ProcessState.Exited) { return; }
            entry.State = ProcessState.Exited; entry.Status = status & 255;
            entry.Stop = null; _active--; _exited.Enqueue(pid);
            while (_exited.Count > _history) { _entries.Remove(_exited.Dequeue()); }
        }
    }
    public IReadOnlyList<ProcessInfo> List(uint uid)
    {
        lock (_gate)
        {
            var result = new List<ProcessInfo>();
            foreach (var entry in _entries.Values) { if (uid == 0 || entry.Uid == uid) { result.Add(entry.Snapshot()); } }
            result.Sort((a, b) => a.Pid.CompareTo(b.Pid)); return result;
        }
    }
    public int RequestStop(int pid, uint uid)
    {
        Action? stop;
        lock (_gate)
        {
            if (!_entries.TryGetValue(pid, out var entry) || entry.State == ProcessState.Exited) { return -3; }
            if (uid != 0 && uid != entry.Uid) { return -13; }
            if (entry.StopRequested) { return 0; }
            entry.StopRequested = true; stop = entry.Stop;
        }
        // Never invoke caller code under the registry lock (terminal and pipe gates
        // have their own ordering). This requests cancellation, never kills a thread.
        stop?.Invoke(); return 0;
    }
}
