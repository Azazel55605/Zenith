using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Processes;

internal readonly record struct ProcessFileInfo(ulong Size, uint Mode, uint Uid, uint Gid);
internal interface IProcessFile : IDisposable
{
    int Read(byte[] buffer);
    (string Name, ProcessFileInfo Info)? Next();
}
internal interface IProcessFiles
{
    IProcessFile Open(string path, bool directory);
    ProcessFileInfo Stat(string path);
}

/// <summary>Per-invocation read-only handles. No global cwd or host descriptor leakage.</summary>
internal sealed class ProcessFiles : IDisposable
{
    public const int MaximumHandles = 16, MaximumIo = 4096, EntryBytes = 280, StatBytes = 24;
    private readonly IProcessFiles _files;
    private readonly Dictionary<int, IProcessFile> _handles = new();
    private readonly string _cwd;
    private int _next = 3;
    public ProcessFiles(IProcessFiles files, string cwd) { _files = files; _cwd = cwd; }
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
        if (flags != 0 && flags != 1) { return -22; } // 0=regular read, 1=directory
        if (_handles.Count == MaximumHandles) { return -24; }
        var file = _files.Open(Resolve(path), flags == 1);
        int fd = _next++; _handles.Add(fd, file); return fd;
    }
    public int Close(int fd)
    {
        if (!_handles.Remove(fd, out var file)) { return -9; }
        file.Dispose(); return 0;
    }
    public int Read(int fd, byte[] buffer)
    {
        if (!_handles.TryGetValue(fd, out var file)) { return -9; }
        return file.Read(buffer);
    }
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
    public void Stat(string path, byte[] buffer) => Encode(_files.Stat(Resolve(path)), buffer);
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
    }
}
