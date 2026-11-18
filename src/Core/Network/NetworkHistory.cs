using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Network;

/// <summary>What happened on the network, for Beacon's history (kept on disk, newest last).</summary>
internal enum NetworkEventKind : uint
{
    Connected = 1, Renewed = 2, Failed = 3, CableOut = 4, CableIn = 5, Disconnected = 6, TurnedOff = 7,
    SettingsChanged = 8, SettingsKept = 9, DnsSlow = 10,
}
internal readonly record struct NetworkEvent(long Time, NetworkEventKind Kind, int Value, string Text);

/// <summary>Beacon's traffic and event history (kernel 0.0.69): per-second traffic for a minute, minute averages for
/// an hour, 15-minute averages for a day and hourly ones for a week, and the last 64 events. Fixed size in memory and
/// on disk; the day, week and events survive restarts (<see cref="Serialize"/>).</summary>
internal sealed class NetworkHistory
{
    public static readonly int[] Counts = { 60, 60, 96, 168 }, Steps = { 1, 60, 900, 3600 };
    public const int MaximumEvents = 64, EventText = 47, Header = 16, EventBytes = 64;
    private readonly object _gate = new();
    private readonly uint[][] _in = { new uint[60], new uint[60], new uint[96], new uint[168] }, _out = { new uint[60], new uint[60], new uint[96], new uint[168] };
    private readonly int[] _next = new int[4], _filled = new int[4], _parts = new int[4];
    private readonly ulong[] _sumIn = new ulong[4], _sumOut = new ulong[4];
    private readonly List<NetworkEvent> _events = new();
    public uint EventTotal { get; private set; }
    /// <summary>Whether something worth saving changed since the last <see cref="Serialize"/>.</summary>
    public bool Dirty { get; private set; }

    /// <summary>One second of traffic in bytes per second.</summary>
    public void Sample(uint rateIn, uint rateOut)
    {
        lock (_gate)
        {
            Store(0, rateIn, rateOut);
            for (int k = 1; k < 4; k++)
            {
                _sumIn[k] += rateIn; _sumOut[k] += rateOut;
                if (++_parts[k] < Steps[k]) { continue; }
                Store(k, (uint)(_sumIn[k] / (ulong)_parts[k]), (uint)(_sumOut[k] / (ulong)_parts[k]));
                _sumIn[k] = _sumOut[k] = 0; _parts[k] = 0;
                if (k >= 2) { Dirty = true; }
            }
        }
    }
    private void Store(int range, uint rateIn, uint rateOut)
    {
        _in[range][_next[range]] = rateIn; _out[range][_next[range]] = rateOut;
        _next[range] = (_next[range] + 1) % Counts[range]; _filled[range] = Math.Min(Counts[range], _filled[range] + 1);
    }
    public void Record(long time, NetworkEventKind kind, int value, string text)
    {
        if (text.Length > EventText) { text = text[..EventText]; }
        var clean = new StringBuilder(text.Length);
        foreach (char c in text) { clean.Append(c is >= ' ' and <= '~' ? c : '?'); }
        lock (_gate)
        {
            if (_events.Count >= MaximumEvents) { _events.RemoveAt(0); }
            _events.Add(new NetworkEvent(time, kind, value, clean.ToString())); EventTotal++; Dirty = true;
        }
    }
    public NetworkEvent? Last { get { lock (_gate) { return _events.Count > 0 ? _events[^1] : null; } } }

    /// <summary>Lines: "V 1", "S range in out" (oldest first, day and week only), "E time kind value text".</summary>
    public void Parse(string text)
    {
        lock (_gate)
        {
            foreach (string raw in text.Split('\n'))
            {
                string[] f = raw.Split(' ', 5);
                if (f.Length == 4 && f[0] == "S" && int.TryParse(f[1], out int range) && range is 2 or 3
                    && uint.TryParse(f[2], out uint i) && uint.TryParse(f[3], out uint o)) { Store(range, i, o); }
                else if (f.Length == 5 && f[0] == "E" && long.TryParse(f[1], out long time) && uint.TryParse(f[2], out uint kind)
                    && int.TryParse(f[3], out int value) && kind is >= 1 and <= 10)
                {
                    if (_events.Count >= MaximumEvents) { _events.RemoveAt(0); }
                    _events.Add(new NetworkEvent(time, (NetworkEventKind)kind, value, f[4].Length > EventText ? f[4][..EventText] : f[4])); EventTotal++;
                }
            }
        }
    }
    public string Serialize()
    {
        var b = new StringBuilder("V 1\n");
        lock (_gate)
        {
            for (int range = 2; range < 4; range++)
            {
                int n = _filled[range], start = (_next[range] - n + Counts[range]) % Counts[range];
                for (int k = 0; k < n; k++) { int at = (start + k) % Counts[range]; b.Append("S ").Append(range).Append(' ').Append(_in[range][at]).Append(' ').Append(_out[range][at]).Append('\n'); }
            }
            foreach (var e in _events) { b.Append("E ").Append(e.Time).Append(' ').Append((uint)e.Kind).Append(' ').Append(e.Value).Append(' ').Append(e.Text).Append('\n'); }
            Dirty = false;
        }
        return b.ToString();
    }
    /// <summary>Traffic of one range: version, range, count, seconds per sample, then count pairs (bytes/s in, out), oldest first.</summary>
    public byte[] Encode(int range)
    {
        if (range is < 0 or > 3) { throw new ArgumentException("range"); }
        lock (_gate)
        {
            int n = _filled[range], start = (_next[range] - n + Counts[range]) % Counts[range];
            var b = new byte[Header + Counts[range] * 8];
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)range);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)n); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)Steps[range]);
            for (int k = 0; k < n; k++)
            {
                int at = (start + k) % Counts[range];
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Header + k * 8), _in[range][at]);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Header + k * 8 + 4), _out[range][at]);
            }
            return b;
        }
    }
    /// <summary>Events, newest first: version, count, total recorded, then 64 bytes each (time, kind, value, text).</summary>
    public byte[] EncodeEvents()
    {
        lock (_gate)
        {
            var b = new byte[Header + MaximumEvents * EventBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)_events.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), EventTotal);
            for (int k = 0; k < _events.Count; k++)
            {
                var e = _events[_events.Count - 1 - k]; var slot = b.AsSpan(Header + k * EventBytes, EventBytes);
                BinaryPrimitives.WriteInt64LittleEndian(slot, e.Time); BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], (uint)e.Kind);
                BinaryPrimitives.WriteInt32LittleEndian(slot[12..], e.Value); Text(slot.Slice(16, 48), e.Text);
            }
            return b;
        }
    }
    public static void Text(Span<byte> s, string text)
    {
        s.Clear(); int n = 0;
        foreach (char ch in text) { if (n + 1 >= s.Length) { break; } s[n++] = ch is >= ' ' and <= '~' ? (byte)ch : (byte)'?'; }
    }
    public static string Duration(long seconds) => seconds < 60 ? seconds + " s" : seconds < 3600 ? seconds / 60 + " min" : seconds / 3600 + " h " + seconds % 3600 / 60 + " min";
}
