using System;
using System.IO;
using Zenith.Core.IO;

namespace Zenith.Core.Processes;

/// <summary>Per-process standard descriptors; callbacks execute only on managed kernel stacks.</summary>
internal sealed class ProcessStreams
{
    private readonly IByteInput _input;
    private readonly Func<int, byte[], int> _write;
    private readonly bool[] _closed = new bool[3];
    private readonly bool[] _terminal;
    private int _readBytes, _written;
    private bool _inputLimit;
    public ProcessStreams(IByteInput input, Func<int, byte[], int> write, bool stdinTerminal, bool stdoutTerminal, bool stderrTerminal)
    { _input = input; _write = write; _terminal = new[] { stdinTerminal, stdoutTerminal, stderrTerminal }; }
    public int Read(byte[] buffer)
    {
        if (_closed[0]) { return -9; }
        if (buffer.Length > ProcessFiles.MaximumIo) { return -22; }
        if (buffer.Length == 0) { return 0; }
        if (_inputLimit) { return -28; }
        if (_readBytes == ProcessFiles.MaximumTransfer)
        {
            // A one-byte overflow probe distinguishes exact-limit EOF from truncation.
            _inputLimit = _input.Read(new byte[1]) != 0; return _inputLimit ? -28 : 0;
        }
        int maximum = Math.Min(buffer.Length, ProcessFiles.MaximumTransfer - _readBytes);
        byte[] target = maximum == buffer.Length ? buffer : new byte[maximum];
        int count = _input.Read(target);
        if (count < 0 || count > target.Length) { throw new IOException("invalid stdin read count"); }
        if (target != buffer) { target.AsSpan(0, count).CopyTo(buffer); }
        _readBytes += count; return count;
    }
    public int Write(int fd, byte[] buffer)
    {
        if (fd is not (1 or 2) || _closed[fd]) { return -9; }
        if (buffer.Length > ProcessFiles.MaximumIo) { return -22; }
        if (buffer.Length > ProcessFiles.MaximumTransfer - _written) { return -28; }
        if (buffer.Length == 0) { return 0; }
        int count = _write(fd, buffer);
        if (count < 0) { return count; }
        if (count > buffer.Length) { throw new IOException("invalid standard output write count"); }
        _written += count; return count;
    }
    public int Close(int fd)
    {
        if (fd < 0 || fd > 2 || _closed[fd]) { return -9; }
        _closed[fd] = true; return 0;
    }
    public int IsTerminal(int fd) => fd < 0 || fd > 2 || _closed[fd] ? -9 : _terminal[fd] ? 1 : 0;
    public void Close() { _closed[0] = _closed[1] = _closed[2] = true; }
}
