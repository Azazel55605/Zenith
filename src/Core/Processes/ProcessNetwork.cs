using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Zenith.Core.Processes;

internal interface IProcessConnection : IDisposable
{
    int Read(byte[] buffer);
    int Write(byte[] buffer);
}
internal interface IProcessDownload : IDisposable
{
    int Write(byte[] buffer);
    void Commit(int expected);
}
internal interface IProcessNetworkSource
{
    string Status();
    string Resolve(string host, string? server, int port);
    long Echo(string host);
    IProcessConnection Connect(string host, int port, bool secure);
    IProcessDownload Download(string path, bool replace, int maximum);
}

/// <summary>Invocation-local handles. No native pointer, PID or environment variable
/// can select another invocation's transport or staging sink.</summary>
internal sealed class ProcessNetwork : IDisposable
{
    public const int MaximumIo = 65536, MaximumTransfer = 32 * 1024 * 1024 + 4096;
    public const int MaximumCalls = 32768;
    private readonly IProcessNetworkSource _source;
    private readonly Dictionary<int, IDisposable> _handles = new();
    private int _next = 10000, _received, _sent, _written, _connections, _downloads, _controls;
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private void Check()
    {
        if (_closed) { throw new IOException("native network context is closed"); }
        if (_lifetime.ElapsedMilliseconds > 900000) { throw new IOException("native network lifetime exceeded 900s"); }
    }
    private void Control()
    { Check(); if (++_controls > 32) { throw new IOException("native network control budget exceeded"); } }
    private bool _closed;
    public string LastError { get; private set; } = "";
    public ProcessNetwork(IProcessNetworkSource source) => _source = source;
    public void Error(string message) => LastError = message.Length <= 512 ? message : message[..512];
    public string Status() { Control(); return _source.Status(); }
    public string Resolve(string host, string? server, int port) { Control(); return _source.Resolve(host, server, port); }
    public long Echo(string host) { Control(); return _source.Echo(host); }
    public int Connect(string host, int port, bool secure)
    {
        if (_closed) { return -9; } Check();
        if (_connections >= 2) { return -28; }
        foreach (var item in _handles.Values) { if (item is IProcessConnection) { return -16; } }
        if (_handles.Count >= 2) { return -24; }
        if (port is < 1 or > 65535) { return -22; }
        _connections++; var connection = _source.Connect(host, port, secure);
        int handle = _next++; _handles.Add(handle, connection); return handle;
    }
    public int Download(string path, bool replace, int maximum)
    {
        if (_closed) { return -9; } Check();
        if (_downloads >= 2) { return -28; }
        if (maximum is < 0 or > MaximumTransfer) { return -22; }
        foreach (var item in _handles.Values) { if (item is IProcessDownload) { return -16; } }
        if (_handles.Count >= 2) { return -24; }
        _downloads++; var download = _source.Download(path, replace, maximum);
        int handle = _next++; _handles.Add(handle, download); return handle;
    }
    public int Read(int handle, byte[] buffer)
    {
        Check(); if (buffer.Length > MaximumIo) { return -22; }
        if (!_handles.TryGetValue(handle, out var item) || item is not IProcessConnection connection) { return -9; }
        int remaining = 2 * (MaximumTransfer + 8192) - _received;
        if (remaining == 0 && buffer.Length > 0) { return -28; }
        byte[] target = buffer.Length <= remaining ? buffer : new byte[remaining];
        int count = connection.Read(target);
        if (count < 0 || count > target.Length) { throw new IOException("invalid native network read count"); }
        if (!ReferenceEquals(target, buffer)) { target.AsSpan(0, count).CopyTo(buffer); }
        _received += count; return count;
    }
    public int Write(int handle, byte[] buffer)
    {
        Check(); if (buffer.Length > MaximumIo) { return -22; }
        if (!_handles.TryGetValue(handle, out var item)) { return -9; }
        int count;
        if (item is IProcessConnection connection)
        {
            if (buffer.Length > 65536 - _sent) { return -28; }
            count = connection.Write(buffer);
            if (count < 0 || count > buffer.Length) { throw new IOException("invalid native network write count"); }
            _sent += count;
        }
        else if (item is IProcessDownload download)
        {
            if (buffer.Length > 2 * MaximumTransfer - _written) { return -28; }
            count = download.Write(buffer);
            if (count < 0 || count > buffer.Length) { throw new IOException("invalid native download write count"); }
            _written += count;
        }
        else { return -9; }
        return count;
    }
    public int Commit(int handle, int expected)
    {
        Check();
        if (!_handles.TryGetValue(handle, out var item) || item is not IProcessDownload download) { return -9; }
        if (expected < 0 || expected > MaximumTransfer) { return -22; }
        download.Commit(expected); _handles.Remove(handle); download.Dispose(); return 0;
    }
    public int Close(int handle)
    {
        if (!_handles.Remove(handle, out var item)) { return -9; }
        item.Dispose(); return 0;
    }
    public void Dispose()
    {
        _closed = true;
        foreach (var item in _handles.Values) { try { item.Dispose(); } catch { } }
        _handles.Clear();
    }
}
