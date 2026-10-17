using System;
using System.IO;
using System.Threading;

namespace Zenith.Core.IO;

internal interface IByteInput { int Read(byte[] buffer); }
internal interface IByteOutput { int Write(byte[] buffer); }
internal sealed class BrokenPipeException : IOException
{
    public BrokenPipeException() : base("broken pipe") { }
}

/// <summary>One reader/one writer, bounded storage. Wait only on managed stacks.</summary>
internal sealed class BytePipe : IByteInput, IByteOutput
{
    public const int Capacity = 8192;
    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[Capacity];
    private readonly Action _cancel;
    private int _head, _count;
    private bool _writerClosed, _readerClosed;
    public BytePipe(Action cancel) => _cancel = cancel;
    public int Buffered { get { lock (_gate) { return _count; } } }
    public int Read(byte[] buffer)
    {
        if (buffer.Length == 0) { return 0; }
        while (true)
        {
            _cancel(); bool closed;
            lock (_gate)
            {
                closed = _readerClosed;
                if (!closed && _count > 0)
                {
                    int count = Math.Min(buffer.Length, _count);
                    int first = Math.Min(count, Capacity - _head);
                    Array.Copy(_buffer, _head, buffer, 0, first);
                    Array.Copy(_buffer, 0, buffer, first, count - first);
                    _head = (_head + count) % Capacity; _count -= count; return count;
                }
                if (!closed && _writerClosed) { return 0; }
            }
            if (closed) { throw new BrokenPipeException(); }
            Thread.Sleep(1);
        }
    }
    public int Write(byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            _cancel(); bool closed;
            lock (_gate)
            {
                closed = _readerClosed || _writerClosed;
                if (!closed)
                {
                    int count = Math.Min(buffer.Length - offset, Capacity - _count);
                    int tail = (_head + _count) % Capacity, first = Math.Min(count, Capacity - tail);
                    Array.Copy(buffer, offset, _buffer, tail, first);
                    Array.Copy(buffer, offset + first, _buffer, 0, count - first);
                    _count += count; offset += count;
                }
            }
            if (closed) { throw new BrokenPipeException(); }
            if (offset < buffer.Length) { Thread.Sleep(1); }
        }
        return offset;
    }
    public void CompleteWriter() { lock (_gate) { _writerClosed = true; } }
    public void CloseReader() { lock (_gate) { _readerClosed = true; _count = 0; } }
    public void Abort() { lock (_gate) { _writerClosed = _readerClosed = true; _count = 0; } }
}
