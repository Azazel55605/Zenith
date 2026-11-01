using System;
using System.Buffers.Binary;

namespace Zenith.Core.Processes;

/// <summary>A kernel-owned shell a client talks to in key events and output, with its caller's credentials.</summary>
internal interface IProcessTerminal
{
    void Key(int code, int modifiers, int extra);
    int Read(Span<byte> destination);
    byte[] State();
    void Resize(int columns);
    void Close();
}

internal interface IProcessTerminalHost
{
    IProcessTerminal? OpenTerminal(int columns);
}

/// <summary>The terminal sessions one program holds. Handles and arguments are checked here; sessions end with it.</summary>
internal sealed class ProcessTerminals : IDisposable
{
    public const int MaximumSessions = 8, MinimumColumns = 20, MaximumColumns = 512, KeyBytes = 12, MaximumRead = 65536;
    public const int StateBytes = 4096, FirstHandle = 30001;
    private readonly IProcessTerminal?[] _sessions = new IProcessTerminal?[MaximumSessions];
    private bool _disposed;

    /// <summary>A handle, or -22 bad columns, -24 too many, -19 no terminal host.</summary>
    public int Open(IProcessTerminalHost? host, int columns)
    {
        if (columns is < MinimumColumns or > MaximumColumns) { return -22; }
        if (_disposed || host is null) { return -19; }
        for (int i = 0; i < _sessions.Length; i++)
        {
            if (_sessions[i] is not null) { continue; }
            var session = host.OpenTerminal(columns);
            if (session is null) { return -19; }
            _sessions[i] = session;
            return FirstHandle + i;
        }
        return -24;
    }

    private IProcessTerminal? Find(long handle)
        => handle >= FirstHandle && handle < FirstHandle + MaximumSessions ? _sessions[handle - FirstHandle] : null;

    public int Key(long handle, ReadOnlySpan<byte> request)
    {
        var session = Find(handle);
        if (session is null) { return -9; }
        if (request.Length != KeyBytes) { return -22; }
        session.Key(BinaryPrimitives.ReadInt32LittleEndian(request), BinaryPrimitives.ReadInt32LittleEndian(request[4..]), BinaryPrimitives.ReadInt32LittleEndian(request[8..]));
        return 0;
    }

    public int Read(long handle, Span<byte> destination) => Find(handle) is { } session ? session.Read(destination) : -9;
    public byte[]? State(long handle) => Find(handle)?.State();

    public int Resize(long handle, int columns)
    {
        var session = Find(handle);
        if (session is null) { return -9; }
        if (columns is < MinimumColumns or > MaximumColumns) { return -22; }
        session.Resize(columns);
        return 0;
    }

    public int Close(long handle)
    {
        var session = Find(handle);
        if (session is null) { return -9; }
        _sessions[handle - FirstHandle] = null;
        session.Close();
        return 0;
    }

    public void Dispose()
    {
        _disposed = true;
        for (int i = 0; i < _sessions.Length; i++) { _sessions[i]?.Close(); _sessions[i] = null; }
    }
}

/// <summary>The session state record: six little-endian u32 fields, then the prompt, input and directory in UTF-8.</summary>
internal static class ProcessTerminalState
{
    public const int Version = 1, Busy = 1, Secret = 2, Raw = 4, Exit = 8, Dropped = 16, HeaderBytes = 24;

    public static byte[] Encode(bool busy, bool secret, bool raw, bool exit, bool dropped, int cursor, string prompt, string input, string directory)
    {
        byte[] first = Bounded(prompt, 512), second = Bounded(input, 2048), third = Bounded(directory, 1024);
        byte[] state = new byte[HeaderBytes + first.Length + second.Length + third.Length];
        int flags = (busy ? Busy : 0) | (secret ? Secret : 0) | (raw ? Raw : 0) | (exit ? Exit : 0) | (dropped ? Dropped : 0);
        BinaryPrimitives.WriteInt32LittleEndian(state, Version);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(4), flags);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(8), cursor);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(12), first.Length);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(16), second.Length);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(20), third.Length);
        first.CopyTo(state, HeaderBytes); second.CopyTo(state, HeaderBytes + first.Length); third.CopyTo(state, HeaderBytes + first.Length + second.Length);
        return state;
    }

    private static byte[] Bounded(string text, int limit)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= limit) { return bytes; }
        int end = limit;
        while (end > 0 && (bytes[end] & 0xc0) == 0x80) { end--; }   // never split a character
        return bytes.AsSpan(0, end).ToArray();
    }
}
