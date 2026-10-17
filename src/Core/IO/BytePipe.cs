using System;
using System.IO;
using System.Threading;

namespace Zenith.Core.IO;

// Byte operations return a count, EOF (0), or a negative errno (-125 cancel, -32 pipe close).
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
    private readonly Func<bool> _cancelled;
    private int _head, _count;
    private bool _writerClosed, _readerClosed;
    public BytePipe(Func<bool> cancelled) => _cancelled = cancelled;
    public int Buffered { get { lock (_gate) { return _count; } } }
    public int Read(byte[] buffer)
    {
        if (buffer.Length == 0) { return 0; }
        while (true)
        {
            if (_cancelled()) { return -125; }
            bool closed;
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
            if (closed) { return -32; }
            Thread.Sleep(1);
        }
    }
    public int Write(byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            if (_cancelled()) { return -125; }
            bool closed;
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
            if (closed) { return -32; }
            if (offset < buffer.Length) { Thread.Sleep(1); }
        }
        return offset;
    }
    public void CompleteWriter() { lock (_gate) { _writerClosed = true; } }
    public void CloseReader() { lock (_gate) { _readerClosed = true; _count = 0; } }
    public void Abort() { lock (_gate) { _writerClosed = _readerClosed = true; _count = 0; } }
}
