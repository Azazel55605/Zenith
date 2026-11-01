using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Zenith.Core.Processes;

// No display or input device pointers cross this boundary. The trusted desktop
// owns admission, session identity, decorations, focus and event delivery.
internal interface IProcessGraphicsHost
{
    ProcessSurface? Open(int width, int height, string title, int format = 1);
}

internal readonly record struct ProcessGraphicsRequest(int Width, int Height, ulong Title, int Length, int Format = 1)
{
    public const int Bytes = 48;
    public static bool TryRead(ReadOnlySpan<byte> b, out ProcessGraphicsRequest r)
    {
        r = default;
        if (b.Length != Bytes || !((BinaryPrimitives.ReadUInt32LittleEndian(b) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) == 0)
                || (BinaryPrimitives.ReadUInt32LittleEndian(b) == 2 && BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) == 1))
            || BinaryPrimitives.ReadUInt64LittleEndian(b[32..]) != 0
            || BinaryPrimitives.ReadUInt64LittleEndian(b[40..]) != 0) { return false; }
        uint w = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]), h = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
        ulong n = BinaryPrimitives.ReadUInt64LittleEndian(b[24..]);
        if (w is < 160 or > 640 || h is < 100 or > 480 || n is < 1 or > 80) { return false; }
        r = new((int)w, (int)h, BinaryPrimitives.ReadUInt64LittleEndian(b[16..]), (int)n, BinaryPrimitives.ReadUInt32LittleEndian(b) == 2 ? 2 : 1); return true;
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
    private ProcessMaterialRegion[] _materials = Array.Empty<ProcessMaterialRegion>();
    public int Format { get; }
    private (int X, int Y, int Width, int Height, int Rise)? _fade;
    private bool _dirty = true, _closed, _disposed, _unsaved, _closeIntent, _presented;
    private bool _pointerMotion, _movePending;
    private int _moveX, _moveY;
    public bool Unsaved { get { lock (_gate) { return _unsaved; } } }
    public ProcessSurface(int width, int height, string title, int format = 1)
    { Width = width; Height = height; Title = title; Format = format; _pixels = new int[width * height];
      if (format == 2) { Array.Fill(_pixels, unchecked((int)0xff000000)); } }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public const int MaximumWidth = 1920, MaximumHeight = 1000;
    private int _requestedWidth, _requestedHeight;
    private ulong _sizeGeneration;
    private bool _resizeEvent;
    public bool Resizable { get; private set; }
    public int EnableResize()
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return -32; }
            if (!Resizable) { Resizable = true; _requestedWidth = Width; _requestedHeight = Height; _sizeGeneration = 1; }
            return 0;
        }
    }
    public int EnablePointerMotion()
    {
        lock (_gate) { if (_closed || _disposed) { return -32; } _pointerMotion = true; return 0; }
    }
    // One replaceable pending position outside the bounded queue: motion can never
    // evict keys or clicks, and a busy pointer costs a client one event per poll.
    public void SendPointerMove(int x, int y)
    {
        lock (_gate)
        {
            if (!_pointerMotion || _closed || _disposed) { return; }
            _moveX = x; _moveY = y; _movePending = true;
        }
    }
    public void SendPointerLeave() => SendPointerMove(-1, -1);
    public void RequestResize(int width, int height)
    {
        lock (_gate)
        {
            if (!Resizable || _closed || _disposed || width < 600 || height < 480 || width > MaximumWidth || height > MaximumHeight
                || (width == _requestedWidth && height == _requestedHeight) || _sizeGeneration == ulong.MaxValue) { return; }
            _requestedWidth = width; _requestedHeight = height; _sizeGeneration++; _resizeEvent = true;
        }
    }
    public byte[] Viewport()
    {
        lock (_gate)
        {
            byte[] b = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Resizable ? 3U : 0U);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), Resizable ? _requestedWidth : Width);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), Resizable ? _requestedHeight : Height);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), _sizeGeneration);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), MaximumWidth);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), MaximumHeight);
            return b;
        }
    }
    public int AcceptResize(ReadOnlySpan<byte> request)
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return -32; }
            if (!Resizable || request.Length != 24 || BinaryPrimitives.ReadUInt32LittleEndian(request) != 1
                || BinaryPrimitives.ReadUInt32LittleEndian(request[4..]) != 0) { return -22; }
            int width = BinaryPrimitives.ReadInt32LittleEndian(request[8..]), height = BinaryPrimitives.ReadInt32LittleEndian(request[12..]);
            if (width != _requestedWidth || height != _requestedHeight || BinaryPrimitives.ReadUInt64LittleEndian(request[16..]) != _sizeGeneration) { return -11; }
            if (width == Width && height == Height) { return 0; }
            var pixels = new int[width * height]; Array.Fill(pixels, unchecked((int)0xff000000));
            _fade = null; Width = width; Height = height; _pixels = pixels; _materials = Array.Empty<ProcessMaterialRegion>(); _dirty = false;
            return 0;
        }
    }
    public (int Width, int Height) FrameSize { get { lock (_gate) { return (Width, Height); } } }
    public string Title { get; }
    public int Bytes => Width * Height * 4;
    public bool Ended { get { lock (_gate) { return _closed || _disposed; } } }
    public bool HasPresented { get { lock (_gate) { return _presented; } } }
    public int Present(byte[] bytes)
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return -32; }
            if (bytes.Length != Bytes || Format != 1) { return -22; }
            // Opaque XRGB8888, fixed row stride. No shared client/compositor pages.
            for (int i = 0; i < _pixels.Length; i++)
            { _pixels[i] = unchecked((int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)) | 0xff000000)); }
            _dirty = _presented = true; return 0;
        }
    }
    public int PresentMaterials(byte[] bytes, ProcessMaterialRegion[] regions)
    {
        lock (_gate)
        {
            if (_closed || _disposed) { return -32; }
            if (Format != 2 || !ProcessMaterialRegion.Validate(regions, Width, Height, Resizable)
                || !ProcessMaterialRegion.ValidForeground(bytes, Width, Height, regions)) { return -22; }
            // Validate the complete frame before replacing anything. Caller buffers
            // and region arrays are never shared with compositor state.
            for (int i = 0; i < _pixels.Length; i++)
            { _pixels[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4)); }
            _materials = (ProcessMaterialRegion[])regions.Clone(); _dirty = _presented = true; return 0;
        }
    }
    /// <summary>Asks for the next presented frame to crossfade from the current one. Validated and fixed-size.</summary>
    public int RequestFade(ReadOnlySpan<byte> request)
    {
        int x = BinaryPrimitives.ReadInt32LittleEndian(request), y = BinaryPrimitives.ReadInt32LittleEndian(request[4..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(request[8..]), height = BinaryPrimitives.ReadInt32LittleEndian(request[12..]);
        int rise = BinaryPrimitives.ReadInt32LittleEndian(request[16..]), reserved = BinaryPrimitives.ReadInt32LittleEndian(request[20..]);
        lock (_gate)
        {
            if (_closed || _disposed) { return -32; }
            if (reserved != 0 || rise is < 0 or > 64 || x < 0 || y < 0 || width < 1 || height < 1 || width > Width - x || height > Height - y) { return -22; }
            _fade = (x, y, width, height, rise); return 0;
        }
    }
    /// <summary>The fade asked for, once its frame has been presented; a later request replaces an earlier one.</summary>
    public (int X, int Y, int Width, int Height, int Rise)? TakeFade()
    {
        lock (_gate)
        {
            if (!_dirty || _fade is null) { return null; }
            var fade = _fade; _fade = null; return fade;
        }
    }
    public bool CopyFrame(int[] destination, bool force)
        => CopyFrame(destination, force, null, out _);
    public bool CopyFrame(int[] destination, bool force, ProcessMaterialRegion[]? materials, out int count)
    {
        lock (_gate)
        {
            count = 0;
            if (_disposed || destination.Length != _pixels.Length || (!_dirty && !force)
                || (materials is not null && materials.Length < _materials.Length)) { return false; }
            Array.Copy(_pixels, destination, _pixels.Length);
            if (materials is not null) { Array.Copy(_materials, materials, _materials.Length); count = _materials.Length; }
            _dirty = false; return true;
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
            if (_resizeEvent) { _resizeEvent = false; return new(10, 0, _requestedWidth, _requestedHeight, 0, 0); }
            if (_events.Count > 0) { return _events.Dequeue(); }
            if (_movePending) { _movePending = false; return new(11, 0, _moveX, _moveY, 0, 0); }
            return default;
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
    public void Dispose() { lock (_gate) { _disposed = true; _closed = true; _events.Clear(); _pixels = Array.Empty<int>(); _materials = Array.Empty<ProcessMaterialRegion>(); } }
}

/// <summary>Paces a client's frames: at most one present per interval, and the empty poll right after a
/// present does not wait again, so an animating client gets the whole interval instead of half of it.</summary>
internal sealed class FramePacer
{
    public const int IntervalMilliseconds = 16;
    private long _next = long.MinValue;
    private bool _presented;

    /// <summary>How long a present arriving at this time waits.</summary>
    public int Present(long now)
    {
        int delay = _next > now ? (int)Math.Min(_next - now, IntervalMilliseconds) : 0;
        _next = now + delay + IntervalMilliseconds;
        _presented = true;
        return delay;
    }

    /// <summary>How long a poll that found nothing waits.</summary>
    public int EmptyPoll()
    {
        if (!_presented) { return IntervalMilliseconds; }
        _presented = false;
        return 0;
    }
}

internal sealed class ProcessGraphics : IDisposable
{
    public const int MaximumCalls = 65536;
    /// <summary>Per renewal minute; above what <see cref="FramePacer"/> allows, so only a stale budget can reach it.</summary>
    public const int MaximumPresents = 7200;
    public FramePacer Pacer { get; } = new();
    public ProcessTerminals Terminals { get; } = new();
    public IProcessTerminalHost? TerminalHost => _host as IProcessTerminalHost;
    private readonly IProcessGraphicsHost? _host;
    private ProcessSurface? _surface;
    private int _handle, _opens, _presents;
    private bool _disposed;
    public bool Live => _surface is not null && !_surface.Ended;
    public bool OwnsLive(int handle) => handle == _handle && Live;
    public void RenewBudget() { _presents = 0; }
    public ProcessGraphics(IProcessGraphicsHost? host) { _host = host; }
    public int Open(int width, int height, string title, int format = 1)
    {
        if (_disposed) { return -9; }
        if (_host is null) { return -19; }
        if (_surface is not null || _opens >= 4) { return -24; }
        if (format is < 1 or > 2 || width is < 160 or > 640 || height is < 100 or > 480 || title.Length is < 1 or > 80) { return -22; }
        foreach (char c in title) { if (c < ' ' || c > '~') { return -22; } }
        var surface = _host.Open(width, height, title, format);
        if (surface is null) { return -19; }
        _surface = surface; _handle = 20000 + ++_opens; return _handle;
    }
    public int EnableResize(int handle) => handle != _handle || _surface is null ? -9 : _surface.EnableResize();
    public int EnablePointerMotion(int handle) => handle != _handle || _surface is null ? -9 : _surface.EnablePointerMotion();
    public byte[]? Viewport(int handle) => handle != _handle || _surface is null || _surface.Ended ? null : _surface.Viewport();
    public int AcceptResize(int handle, ReadOnlySpan<byte> request) => handle != _handle || _surface is null ? -9 : _surface.AcceptResize(request);
    public int RequestFade(int handle, ReadOnlySpan<byte> request) => handle != _handle || _surface is null ? -9 : _surface.RequestFade(request);
    public int Dirty(int handle, bool dirty) => handle != _handle || _surface is null ? -9 : _surface.SetDirty(dirty);
    public int Size(int handle) => handle != _handle || _surface is null ? -9 : _presents >= MaximumPresents ? -27 : _surface.Bytes;
    public int Present(int handle, byte[] pixels)
    {
        if (handle != _handle || _surface is null) { return -9; }
        if (++_presents > MaximumPresents) { return -27; }
        return _surface.Present(pixels);
    }
    public bool SupportsMaterials => !_disposed && _host is not null;
    public int PresentMaterials(int handle, byte[] pixels, ProcessMaterialRegion[] regions)
    {
        if (handle != _handle || _surface is null) { return -9; }
        if (++_presents > MaximumPresents) { return -27; }
        return _surface.PresentMaterials(pixels, regions);
    }
    public ProcessGraphicsEvent? Poll(int handle) => handle == _handle && _surface is not null ? _surface.Poll() : null;
    public int Close(int handle)
    {
        if (handle != _handle || _surface is null) { return -9; }
        _surface.Dispose(); _surface = null; return 0;
    }
    public void Dispose() { _disposed = true; Terminals.Dispose(); _surface?.Dispose(); _surface = null; }
}
