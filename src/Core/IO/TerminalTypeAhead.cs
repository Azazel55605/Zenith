using System;
using System.Collections.Generic;

namespace Zenith.Core.IO;

/// <summary>GUI-owned type-ahead: lines and EOF retain their order across worker reads.</summary>
internal sealed class TerminalTypeAhead
{
    private readonly Queue<string?> _items = new();
    public bool HasPending => _items.Count > 0;
    public void Enqueue(string line) => _items.Enqueue(line);
    public void EndInput() => _items.Enqueue(null);
    public void Clear() => _items.Clear();

    // The worker may still be consuming its previous reply. Retain the next
    // item until its next read actually accepts it; never discard an early EOF.
    public bool TryAnswer(Func<string?, bool> answer)
    {
        if (_items.Count == 0 || !answer(_items.Peek())) { return false; }
        _items.Dequeue();
        return true;
    }

    // EOF belongs to a foreground read, not to the next command at the prompt.
    public bool TryTakeCommand(out string command)
    {
        while (_items.Count > 0)
        {
            string? item = _items.Dequeue();
            if (item is not null) { command = item; return true; }
        }
        command = string.Empty;
        return false;
    }
}
