using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Processes;

internal readonly record struct ProcessFileInfo(ulong Size, uint Mode, uint Uid, uint Gid);
internal interface IProcessFile : IDisposable
{
    int Read(byte[] buffer);
    int Write(byte[] buffer);
    int Flush();
    (string Name, ProcessFileInfo Info)? Next();
}
internal interface IProcessFiles
{
    IProcessFile Open(string path, ulong flags);
    ProcessFileInfo Stat(string path);
}

/// <summary>Bounded per-invocation descriptors and finite stdin; no host descriptor leakage.</summary>
internal sealed class ProcessFiles : IDisposable
{
    public const int MaximumHandles = 16, MaximumIo = 4096, EntryBytes = 280, StatBytes = 24;
    public const int MaximumTransfer = 1048576;
    public const ulong WriteOnly = 2, Create = 4, Truncate = 8, Append = 16;
    private readonly IProcessFiles _files;
    private readonly Dictionary<int, IProcessFile> _handles = new();
    private readonly string _cwd;
    private readonly byte[] _input;
    private int _inputOffset, _written;
    private readonly ProcessStreams? _streams;
    private bool _inputClosed;
    private int _next = 3;
    public ProcessFiles(IProcessFiles files, string cwd, string? input = null, ProcessStreams? streams = null)
    {
        _files = files; _cwd = cwd; _streams = streams;
        if (Encoding.UTF8.GetByteCount(input ?? "") > MaximumTransfer) { throw new IOException("native stdin exceeds 1 MiB"); }
        _input = Encoding.UTF8.GetBytes(input ?? "");
    }
    public string Cwd => _cwd;
    public string Resolve(string path)
    {
        if (path.Length == 0 || path.Length > 1024 || path.IndexOf('\0') >= 0) { throw new ArgumentException("invalid native path"); }
        // Preserve .. until VFS traversal: lexical collapsing could bypass an
        // inaccessible ancestor or change symlink/.. semantics.
        return path[0] == '/' ? path : _cwd.TrimEnd('/') + "/" + path;
    }
    public int Open(string path, ulong flags)
    {
        if (!ValidFlags(flags)) { return -22; }
        if (_handles.Count == MaximumHandles) { return -24; }
        var file = _files.Open(Resolve(path), flags);
        int fd = _next++; _handles.Add(fd, file); return fd;
    }
    public int Close(int fd)
    {
        if (_streams is not null && fd is >= 0 and <= 2) { return _streams.Close(fd); }
        if (fd == 0) { if (_inputClosed) { return -9; } _inputClosed = true; return 0; }
        if (!_handles.Remove(fd, out var file)) { return -9; }
        file.Dispose(); return 0;
    }
    public int Read(int fd, byte[] buffer)
    {
        if (buffer.Length > MaximumIo) { return -22; }
        if (fd == 0)
        {
            if (_streams is not null) { return _streams.Read(buffer); }
            if (_inputClosed) { return -9; }
            int count = Math.Min(buffer.Length, _input.Length - _inputOffset);
            _input.AsSpan(_inputOffset, count).CopyTo(buffer); _inputOffset += count; return count;
        }
        if (!_handles.TryGetValue(fd, out var file)) { return -9; }
        return file.Read(buffer);
    }
    public static bool ValidFlags(ulong flags) => flags is 0 or 1
        || ((flags & WriteOnly) != 0 && (flags & ~(WriteOnly | Create | Truncate | Append)) == 0
            && (flags & (Truncate | Append)) != (Truncate | Append));
    public int Write(int fd, byte[] buffer)
    {
        if (_streams is not null && fd is 1 or 2) { return _streams.Write(fd, buffer); }
        if (!_handles.TryGetValue(fd, out var file)) { return -9; }
        if (buffer.Length > MaximumIo) { return -22; }
        if (buffer.Length > MaximumTransfer - _written) { return -28; }
        int result = file.Write(buffer);
        if (result < 0) { return result; }
        if (result > buffer.Length) { throw new IOException("invalid native write count"); }
        _written += result; return result;
    }
    public int TerminalMode(int fd, int? flags = null) => _streams?.TerminalMode(fd, flags) ?? (fd == 0 && !_inputClosed ? -25 : -9);
    public int OpenCopyDestination(int source, string path)
    {
        if (!_handles.TryGetValue(source, out var file)) { return -9; }
        if (_handles.Count == MaximumHandles) { return -24; }
        if (_files is not IProcessCopyFiles copying) { return -38; }
        var target = copying.OpenCopyDestination(Resolve(path), file);
        int fd = _next++; _handles.Add(fd, target); return fd;
    }
    public int Mutate(ProcessPathOperation operation, string path, string? target, ulong value, ulong extra)
    {
        if (_files is not IProcessNamespace space) { return -38; }
        return space.Mutate(operation, Resolve(path), target is null ? null : operation == ProcessPathOperation.Symlink ? target : Resolve(target), value, extra);
    }
    public long Seek(int fd, long offset, int origin)
    {
        if (!_handles.TryGetValue(fd, out var file)) { return -9; }
        if (origin is < 0 or > 2) { return -22; }
        return file is ISeekableProcessFile seekable ? seekable.Seek(offset, origin) : -29;
    }
    public int IsTerminal(int fd) => _streams?.IsTerminal(fd) ?? (fd == 0 && !_inputClosed ? 0 : -9);
    public int Flush(int fd) => _handles.TryGetValue(fd, out var file) ? file.Flush() : -9;
    public int Next(int fd, byte[] buffer)
    {
        if (!_handles.TryGetValue(fd, out var file)) { return -9; }
        if (buffer.Length < EntryBytes) { return -22; }
        var entry = file.Next();
        if (entry is null) { return 0; }
        byte[] name = Encoding.UTF8.GetBytes(entry.Value.Name);
        if (name.Length > 255 || entry.Value.Name.IndexOf('\0') >= 0) { return -36; }
        buffer.AsSpan().Clear(); Encode(entry.Value.Info, buffer);
        name.CopyTo(buffer, StatBytes); return EntryBytes;
    }
    public void Stat(string path, byte[] buffer, bool follow = true)
    {
        string resolved = Resolve(path);
        var info = follow ? _files.Stat(resolved) : _files is IProcessLinkMetadata links ? links.LStat(resolved) : throw new NotSupportedException("no-follow metadata unavailable");
        Encode(info, buffer);
    }
    private static void Encode(ProcessFileInfo value, byte[] buffer)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer, value.Size);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), value.Mode);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), value.Uid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), value.Gid);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), 0);
    }
    public void Dispose()
    {
        foreach (var file in _handles.Values) { try { file.Dispose(); } catch { /* Continue releasing other handles on teardown. */ } }
        _handles.Clear();
        _inputClosed = true; _streams?.Close();
    }
}
