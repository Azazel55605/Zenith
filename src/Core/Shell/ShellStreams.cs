using System;
using System.IO;
using System.Text;
using Zenith.Core.IO;

namespace Zenith.Core.Shell;

internal sealed class PipeOutput : IOutput, IByteOutput
{
    private readonly BytePipe _pipe;
    public PipeOutput(BytePipe pipe) => _pipe = pipe;
    // Builtins retain their plain-text pipe contract; native writes use bytes directly.
    public void Write(string text)
    {
        int count = _pipe.Write(Encoding.UTF8.GetBytes(Ansi.Strip(text)));
        if (count == -32) { throw new BrokenPipeException(); }
        // Cancellation is observed by the shell; do not throw on multiple workers.
    }
    public int Write(byte[] buffer) => _pipe.Write(buffer);
}

/// <summary>Incremental UTF-8 decoding at text terminals, byte-exact native pipelines.</summary>
internal sealed class CommandByteOutput
{
    private readonly IOutput _output;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    public CommandByteOutput(IOutput output) => _output = output;
    public int Write(byte[] buffer)
    {
        if (_output is IByteOutput bytes) { return bytes.Write(buffer); }
        char[] text = new char[buffer.Length + 2];
        int count = _decoder.GetChars(buffer, 0, buffer.Length, text, 0, false);
        if (count > 0) { _output.Write(new string(text, 0, count)); }
        return buffer.Length;
    }
    public void Complete()
    {
        if (_output is IByteOutput) { return; }
        char[] text = new char[2]; int count = _decoder.GetChars(Array.Empty<byte>(), 0, 0, text, 0, true);
        if (count > 0) { _output.Write(new string(text, 0, count)); }
    }
}

internal sealed class LineByteInput : IByteInput
{
    private readonly Func<string?> _readLine;
    private readonly Func<bool> _cancelled;
    private byte[] _line = Array.Empty<byte>();
    private int _offset;
    private bool _eof;
    public LineByteInput(Func<string?> readLine, Func<bool> cancelled) { _readLine = readLine; _cancelled = cancelled; }
    public int Read(byte[] buffer)
    {
        if (_cancelled()) { return -125; }
        if (buffer.Length == 0) { return 0; }
        if (_offset == _line.Length && !_eof)
        {
            string? line = _readLine(); if (_cancelled()) { return -125; }
            if (line is null) { _eof = true; }
            else
            {
                if (Encoding.UTF8.GetByteCount(line) > 16384) { throw new IOException("terminal input line exceeds 16 KiB"); }
                _line = Encoding.UTF8.GetBytes(line + "\n"); _offset = 0;
            }
        }
        int count = Math.Min(buffer.Length, _line.Length - _offset);
        _line.AsSpan(_offset, count).CopyTo(buffer); _offset += count; return count;
    }
}

/// <summary>Compatibility redirections commit after a command, bounded and byte-exact.</summary>
internal sealed class RedirectOutput : IOutput, IByteOutput
{
    private readonly MemoryStream _bytes = new();
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(Ansi.Strip(text)));
    public int Write(byte[] bytes)
    {
        if (bytes.Length > ShellInput.MaximumText - _bytes.Length) { throw new IOException("redirected output exceeds 1 MiB"); }
        _bytes.Write(bytes, 0, bytes.Length); return bytes.Length;
    }
    public byte[] Bytes => _bytes.ToArray();
}
