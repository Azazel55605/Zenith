using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Monitoring;

/// <summary>A disk or network interface: cumulative bytes in (read, received) and out (written,
/// sent) since boot. Network interfaces add link flags, the IPv4 address and the MAC address.</summary>
internal readonly record struct IoDevice(string Name, ulong In, ulong Out, uint Flags = 0, uint Address = 0, ulong Mac = 0);

/// <summary>A window on the desktop and the program that owns it (0: the desktop itself).</summary>
internal readonly record struct ActivityWindow(int Pid, string Title, bool Focused, bool Minimized);

internal interface IActivitySources
{
    long Milliseconds { get; }
    IReadOnlyList<IoDevice> Disks();
    IReadOnlyList<IoDevice> Networks();
}

/// <summary>
/// Observatory's disk, network and apps view (call 649): per-device totals and rates, two minutes of
/// combined rates, and the windows with their programs. Sampled at most once a second, like
/// <see cref="SystemMonitor"/>; reading never blocks on a driver or the desktop.
/// </summary>
internal sealed class ActivityMonitor
{
    public const int SnapshotBytes = 4096, History = 120, MaximumDisks = 8, MaximumNetworks = 4, MaximumWindows = 24;
    public const int DisksOffset = 32, DiskBytes = 48, NetworksOffset = 416, NetworkBytes = 56, HistoryOffset = 640, SampleBytes = 16;
    public const int WindowsOffset = 2560, WindowBytes = 64, IntervalMs = 1000;
    public const uint LinkUp = 1, Ready = 2, Primary = 4, Focused = 1, Minimized = 2;
    private readonly IActivitySources _sources;
    private readonly object _gate = new();
    private readonly uint[] _ring = new uint[History * 4];
    private int _count, _next;
    private long _sampled = long.MinValue;
    private IoDevice[] _disks = Array.Empty<IoDevice>(), _networks = Array.Empty<IoDevice>();
    private (uint In, uint Out)[] _diskRates = Array.Empty<(uint, uint)>(), _networkRates = Array.Empty<(uint, uint)>();
    public ActivityMonitor(IActivitySources sources) { _sources = sources; }

    public void Tick()
    {
        long now = _sources.Milliseconds;
        lock (_gate)
        {
            if (_sampled != long.MinValue && now - _sampled < IntervalMs) { return; }
            IoDevice[] disks = Limit(_sources.Disks(), MaximumDisks), networks = Limit(_sources.Networks(), MaximumNetworks);
            long elapsed = _sampled == long.MinValue ? 0 : now - _sampled;
            var diskRates = Rates(_disks, disks, elapsed); var networkRates = Rates(_networks, networks, elapsed);
            if (elapsed > 0)
            {
                ulong read = 0, written = 0, received = 0, sent = 0;
                foreach (var r in diskRates) { read += r.In; written += r.Out; }
                foreach (var r in networkRates) { received += r.In; sent += r.Out; }
                int at = _next * 4;
                _ring[at] = Clamp(read); _ring[at + 1] = Clamp(written); _ring[at + 2] = Clamp(received); _ring[at + 3] = Clamp(sent);
                _next = (_next + 1) % History; _count = Math.Min(History, _count + 1);
            }
            _disks = disks; _networks = networks; _diskRates = diskRates; _networkRates = networkRates; _sampled = now;
        }
    }

    private static IoDevice[] Limit(IReadOnlyList<IoDevice> devices, int maximum)
    {
        var result = new IoDevice[Math.Min(maximum, devices.Count)];
        for (int i = 0; i < result.Length; i++) { result[i] = devices[i]; }
        return result;
    }

    /// <summary>Bytes per second since the last sample, matched by name (a new device starts at 0;
    /// a counter that went backwards, a replaced device, reads 0).</summary>
    private static (uint In, uint Out)[] Rates(IoDevice[] before, IoDevice[] now, long elapsed)
    {
        var rates = new (uint In, uint Out)[now.Length];
        if (elapsed <= 0) { return rates; }
        for (int i = 0; i < now.Length; i++)
        {
            foreach (var old in before)
            {
                if (old.Name != now[i].Name) { continue; }
                ulong a = now[i].In >= old.In ? now[i].In - old.In : 0, b = now[i].Out >= old.Out ? now[i].Out - old.Out : 0;
                rates[i] = (Clamp(a * 1000 / (ulong)elapsed), Clamp(b * 1000 / (ulong)elapsed));
                break;
            }
        }
        return rates;
    }

    private static uint Clamp(ulong v) => v > uint.MaxValue ? uint.MaxValue : (uint)v;

    public byte[] Snapshot(IReadOnlyList<ActivityWindow> windows)
    {
        Tick();
        var data = new byte[SnapshotBytes]; Span<byte> b = data;
        lock (_gate)
        {
            int shownWindows = Math.Min(MaximumWindows, windows.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)_disks.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b[8..], (uint)_networks.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(b[12..], (uint)_count);
            BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)shownWindows);
            BinaryPrimitives.WriteUInt32LittleEndian(b[20..], IntervalMs);
            BinaryPrimitives.WriteUInt32LittleEndian(b[24..], _count == 0 ? 0 : (uint)Math.Clamp(_sources.Milliseconds - _sampled, 0, 60000));
            for (int i = 0; i < _disks.Length; i++)
            {
                Span<byte> d = b.Slice(DisksOffset + i * DiskBytes, DiskBytes);
                Text(d[..16], _disks[i].Name);
                BinaryPrimitives.WriteUInt64LittleEndian(d[16..], _disks[i].In);
                BinaryPrimitives.WriteUInt64LittleEndian(d[24..], _disks[i].Out);
                BinaryPrimitives.WriteUInt32LittleEndian(d[32..], _diskRates[i].In);
                BinaryPrimitives.WriteUInt32LittleEndian(d[36..], _diskRates[i].Out);
            }
            for (int i = 0; i < _networks.Length; i++)
            {
                Span<byte> n = b.Slice(NetworksOffset + i * NetworkBytes, NetworkBytes);
                Text(n[..16], _networks[i].Name);
                BinaryPrimitives.WriteUInt64LittleEndian(n[16..], _networks[i].In);
                BinaryPrimitives.WriteUInt64LittleEndian(n[24..], _networks[i].Out);
                BinaryPrimitives.WriteUInt32LittleEndian(n[32..], _networkRates[i].In);
                BinaryPrimitives.WriteUInt32LittleEndian(n[36..], _networkRates[i].Out);
                BinaryPrimitives.WriteUInt32LittleEndian(n[40..], _networks[i].Flags);
                BinaryPrimitives.WriteUInt32BigEndian(n[44..], _networks[i].Address);
                for (int k = 0; k < 6; k++) { n[48 + k] = (byte)(_networks[i].Mac >> (40 - 8 * k)); }
            }
            // Oldest first, as in the system snapshot.
            for (int i = 0; i < _count; i++)
            {
                int slot = (_next - _count + i + History) % History;
                for (int k = 0; k < 4; k++) { BinaryPrimitives.WriteUInt32LittleEndian(b[(HistoryOffset + i * SampleBytes + k * 4)..], _ring[slot * 4 + k]); }
            }
            for (int i = 0; i < shownWindows; i++)
            {
                Span<byte> w = b.Slice(WindowsOffset + i * WindowBytes, WindowBytes);
                BinaryPrimitives.WriteInt32LittleEndian(w, windows[i].Pid);
                BinaryPrimitives.WriteUInt32LittleEndian(w[4..], (windows[i].Focused ? Focused : 0) | (windows[i].Minimized ? Minimized : 0));
                Utf8(w.Slice(8, 56), windows[i].Title);
            }
        }
        return data;
    }

    /// <summary>UTF-8 (titles carry "—" and the like), cut on a character boundary to leave a
    /// terminating zero; control characters become '?'.</summary>
    private static void Utf8(Span<byte> target, string value)
    {
        int n = 0; Span<byte> one = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            Rune r = rune.Value < 32 || rune.Value == 127 ? new Rune('?') : rune;
            int k = r.EncodeToUtf8(one);
            if (n + k > target.Length - 1) { break; }
            one[..k].CopyTo(target[n..]); n += k;
        }
    }

    /// <summary>Printable ASCII, cut to leave a terminating zero.</summary>
    private static void Text(Span<byte> target, string value)
    {
        int n = 0;
        foreach (char c in value)
        {
            if (n >= target.Length - 1) { break; }
            target[n++] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?';
        }
    }
}
