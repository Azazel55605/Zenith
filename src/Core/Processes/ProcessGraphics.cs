using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Zenith.Core.Processes;

// No display or input device pointers cross this boundary. The trusted desktop
// owns admission, session identity, decorations, focus and event delivery.
internal interface IProcessGraphicsHost
{
    ProcessSurface? Open(int width, int height, string title);
}

internal readonly record struct ProcessGraphicsRequest(int Width, int Height, ulong Title, int Length)
{
    public const int Bytes = 48;
    public static bool TryRead(ReadOnlySpan<byte> b, out ProcessGraphicsRequest r)
    {
        r = default;
        if (b.Length != Bytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(b[32..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(b[40..]) != 0) { return false; }
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]), h = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
        ulong n = BinaryPrimitives.ReadUInt64LittleEndian(b[24..]);
        if (w is < 160 or > 640 || h is < 100 or > 480 || n is < 1 or > 80) { return false; }
        r = new((int)w, (int)h, BinaryPrimitives.ReadUInt64LittleEndian(b[16..]), (int)n); return true;
    }
}

// Stable v1 events, independent of Cosmos enums. Key code is Unicode text;
// extra holds a stable special-key code. Pointer coordinates are content-local.
internal readonly record struct ProcessGraphicsEvent(int Type, int Code, int X, int Y, int Modifiers, int Extra)
{
    public const int Bytes = 24;
    public byte[] Encode()
    {
        byte[] b = new byte[Bytes];
        int[] words = { Type, Code, X, Y, Modifiers, Extra };
        for (int i = 0; i < words.Length; i++) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), words[i]); }
        return b;
    }
}

internal sealed class ProcessSurface : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<ProcessGraphicsEvent> _events = new();
    private int[] _pixels;
    private bool _dirty = true, _closed, _disposed, _unsaved, _closeIntent;
    public bool Unsaved { get { lock (_gate) { return _unsaved; } } }
    public ProcessSurface(int width, int height, string title)
    { Width = width; Height = height; Title = title; _pixels = new int[width * height]; }
    public int Width { get; }
    public int Height { get; }
    public string Title { get; }
    public int Bytes => Width * Height * 4;
    public bool Ended { get { lock (_gate) { return _closed || _disposed; } } }
    public int Present(byte[] bytes)
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return -32; }
            if (bytes.Length != Bytes) { return -22; }
            // Opaque XRGB8888, fixed row stride. No shared client/compositor pages.
            for (int i = 0; i < _pixels.Length; i++)
            { _pixels[i] = unchecked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)) | 0xff000000)); }
            _dirty = true; return 0;
        }
    }
    public bool CopyFrame(int[] destination, bool force)
    {
        lock (_gate)
        {
            if (_disposed || destination.Length != _pixels.Length || (!_dirty && !force)) { return false; }
            Array.Copy(_pixels, destination, _pixels.Length); _dirty = false; return true;
        }
    }
    public void Send(ProcessGraphicsEvent e)
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return; }
            if (_events.Count >= 64)
            {
                // Drop stale input and explicitly reset client input state. Close
                // is sticky and cannot be evicted by a flood of pointer events.
                _events.Clear(); _events.Enqueue(new(7, 0, 0, 0, 0, 0));
            }
            _events.Enqueue(e);
        }
    }
    public ProcessGraphicsEvent Poll()
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return new(1, 0, 0, 0, 0, 0); }
            if (_closeIntent) { _closeIntent = false; return new(9, 0, 0, 0, 0, 0); }
            return _events.Count == 0 ? default : _events.Dequeue();
        }
    }
    public int SetDirty(bool dirty) { lock (_gate) { if (_closed || _disposed) { return -32; } _unsaved = dirty; return 0; } }
    public bool RequestClose()
    {
        lock (_gate)
        {
            if (_unsaved && !_disposed) { _closeIntent = true; return false; }
            _closed = true; _events.Clear(); return true;
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _closed = true; _events.Clear(); _pixels = Array.Empty<int>(); } }
}

internal sealed class ProcessGraphics : IDisposable
{
    public const int MaximumCalls = 65536;
    private readonly IProcessGraphicsHost? _host;
    private ProcessSurface? _surface;
    private int _handle, _opens, _presents;
    private bool _disposed;
    public ProcessGraphics(IProcessGraphicsHost? host) { _host = host; }
    public int Open(int width, int height, string title)
    {
        if (_disposed) { return -9; }
        if (_host is null) { return -19; }
        if (_surface is not null || _opens >= 4) { return -24; }
        if (width is < 160 or > 640 || height is < 100 or > 480 || title.Length is < 1 or > 80) { return -22; }
        foreach (char c in title) { if (c < ' ' || c > '~') { return -22; } }
        var surface = _host.Open(width, height, title);
        if (surface is null) { return -19; }
        _surface = surface; _handle = 20000 + ++_opens; return _handle;
    }
    public int Dirty(int handle, bool dirty) => handle != _handle || _surface is null ? -9 : _surface.SetDirty(dirty);
    public int Size(int handle) => handle != _handle || _surface is null ? -9 : _presents >= 3600 ? -27 : _surface.Bytes;
    public int Present(int handle, byte[] pixels)
    {
        if (handle != _handle || _surface is null) { return -9; }
        if (++_presents > 3600) { return -27; }
        return _surface.Present(pixels);
    }
    public ProcessGraphicsEvent? Poll(int handle) => handle == _handle && _surface is not null ? _surface.Poll() : null;
    public int Close(int handle)
    {
        if (handle != _handle || _surface is null) { return -9; }
        _surface.Dispose(); _surface = null; return 0;
    }
    public void Dispose() { _disposed = true; _surface?.Dispose(); _surface = null; }
}
