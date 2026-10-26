using System;
using System.Threading;

namespace Zenith.Core.Processes;

/// <summary>A stop request belongs to one invocation, never the next terminal command.</summary>
internal sealed class ProcessCancellation(Action cancelForeground)
{
    private readonly object _gate = new();
    private bool _active = true;
    private int _requested;
    public bool Requested => Volatile.Read(ref _requested) != 0;
    public void Request()
    {
        lock (_gate)
        {
            if (!_active || _requested != 0) { return; }
            Volatile.Write(ref _requested, 1); cancelForeground();
        }
    }
    public void Close() { lock (_gate) { _active = false; } }
}
