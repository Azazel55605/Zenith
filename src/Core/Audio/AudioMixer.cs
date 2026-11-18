using System;

namespace Zenith.Core.Audio;

/// <summary>Fixed-memory stereo S16LE mixer and capture fan-out. AudioService serializes it.
/// Every handle owns its queue; no backlog or allocation is proportional to uptime.</summary>
internal sealed class AudioMixer
{
    public const int MaximumStreams = 16, QueueBytes = 32768, PeriodBytes = 4096;
    internal sealed class Stream
    {
        public readonly long Handle;
        public readonly int Pid, Direction;
        public readonly uint Uid;
        public readonly string Name;
        public readonly byte[] Buffer = new byte[QueueBytes];
        public int Head, Count, Volume = 100, Peak;
        public bool Muted;
        public long Dropped;
        public Stream(long handle, int pid, uint uid, string name, int direction)
        { Handle = handle; Pid = pid; Uid = uid; Name = name; Direction = direction; }
    }
    public readonly Stream?[] Streams = new Stream?[MaximumStreams];
    private long _next;
    public long Open(int pid, uint uid, string name, int direction)
    {
        if (direction is < 0 or > 1) { return -22; }
        for (int i = 0; i < Streams.Length; i++)
        {
            if (Streams[i] is not null) { continue; }
            if (_next == long.MaxValue) { return -24; }
            var stream = new Stream(++_next, pid, uid, name, direction);
            Streams[i] = stream;
            return stream.Handle;
        }
        return -24;
    }
    public Stream? Find(long handle)
    {
        foreach (var stream in Streams) { if (stream?.Handle == handle) { return stream; } }
        return null;
    }
    public bool Active(int direction)
    {
        foreach (var stream in Streams) { if (stream?.Direction == direction) { return true; } }
        return false;
    }
    public int Close(int pid, long handle)
    {
        for (int i = 0; i < Streams.Length; i++)
        {
            if (Streams[i]?.Handle != handle) { continue; }
            if (Streams[i]!.Pid != pid) { return -9; }
            Streams[i] = null;
            return 0;
        }
        return -9;
    }
    public void Release(int pid)
    {
        for (int i = 0; i < Streams.Length; i++) { if (Streams[i]?.Pid == pid) { Streams[i] = null; } }
    }
    public int Transfer(int pid, long handle, Span<byte> data, bool write)
    {
        var stream = Find(handle);
        if (stream is null || stream.Pid != pid) { return -9; }
        if (stream.Direction != (write ? 0 : 1) || data.Length is < 4 or > 8192 || data.Length % 4 != 0) { return -22; }
        int count = Math.Min(data.Length, write ? QueueBytes - stream.Count : stream.Count) & ~3;
        if (count == 0) { return -11; }
        for (int i = 0; i < count; i++)
        {
            if (write) { stream.Buffer[(stream.Head + stream.Count + i) % QueueBytes] = data[i]; }
            else { data[i] = stream.Buffer[(stream.Head + i) % QueueBytes]; }
        }
        if (write) { stream.Count += count; }
        else { stream.Head = (stream.Head + count) % QueueBytes; stream.Count -= count; }
        return count;
    }
    private static int Sample(byte lo, byte hi) => (short)(lo | hi << 8);
    public int Mix(Span<byte> output, int volume, bool muted)
    {
        output.Clear();
        foreach (var stream in Streams) { if (stream?.Direction == 0) { stream.Peak = 0; } }
        int peak = 0;
        for (int i = 0; i + 3 < output.Length; i += 4)
        {
            int left = 0, right = 0;
            foreach (var stream in Streams)
            {
                if (stream is null || stream.Direction != 0 || stream.Count < 4) { continue; }
                int h = stream.Head;
                int a = Sample(stream.Buffer[h], stream.Buffer[(h + 1) % QueueBytes]);
                int b = Sample(stream.Buffer[(h + 2) % QueueBytes], stream.Buffer[(h + 3) % QueueBytes]);
                stream.Head = (h + 4) % QueueBytes; stream.Count -= 4;
                if (stream.Muted) { continue; }
                a = a * stream.Volume / 100; b = b * stream.Volume / 100;
                stream.Peak = Math.Max(stream.Peak, Math.Max(Math.Abs(a), Math.Abs(b)));
                left += a; right += b;
            }
            left = muted ? 0 : Math.Clamp(left * volume / 100, short.MinValue, short.MaxValue);
            right = muted ? 0 : Math.Clamp(right * volume / 100, short.MinValue, short.MaxValue);
            peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
            output[i] = (byte)left; output[i + 1] = (byte)(left >> 8);
            output[i + 2] = (byte)right; output[i + 3] = (byte)(right >> 8);
        }
        return peak;
    }
    public int Capture(ReadOnlySpan<byte> input, int volume, bool muted)
    {
        int peak = 0;
        foreach (var stream in Streams)
        {
            if (stream is null || stream.Direction != 1) { continue; }
            stream.Peak = 0;
            for (int i = 0; i + 3 < input.Length; i += 4)
            {
                // Prefer current input to old audio if a reader falls behind. Whole frames only.
                if (stream.Count == QueueBytes) { stream.Head = (stream.Head + 4) % QueueBytes; stream.Count -= 4; stream.Dropped++; }
                int h = (stream.Head + stream.Count) % QueueBytes;
                for (int channel = 0; channel < 2; channel++)
                {
                    int sample = Sample(input[i + channel * 2], input[i + channel * 2 + 1]);
                    sample = muted || stream.Muted ? 0 : sample * volume / 100 * stream.Volume / 100;
                    stream.Peak = Math.Max(stream.Peak, Math.Abs(sample));
                    stream.Buffer[(h + channel * 2) % QueueBytes] = (byte)sample;
                    stream.Buffer[(h + channel * 2 + 1) % QueueBytes] = (byte)(sample >> 8);
                }
                stream.Count += 4;
            }
            peak = Math.Max(peak, stream.Peak);
        }
        return peak;
    }
}
