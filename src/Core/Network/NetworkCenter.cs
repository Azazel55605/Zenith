using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Cosmos.Kernel.System.Network;
using Zenith.Core.Network.Transport;
using Zenith.Core.Storage;
using Zenith.Core.Time;

namespace Zenith.Core.Network;

/// <summary>Beacon's view of the network (kernel 0.0.69, call 667, docs/components/BEACON.md): structured status,
/// traffic at four resolutions, a bounded event history that survives restarts, diagnostics step by step, ping and
/// a safe apply. Sampling runs from <see cref="NetworkService.Tick"/> (the kernel loop, with or without a desktop)
/// and only touches counters; the disk is written by a background thread.</summary>
internal static class NetworkCenter
{
    public const string HistoryPath = "/var/lib/zenith/network-history";
    public static readonly NetworkHistory History = new();
    private static readonly object s_gate = new();
    private static long s_lastSample = long.MinValue, s_bytesIn, s_bytesOut, s_lastDnsSlow;
    private static uint s_rateIn, s_rateOut;
    private static bool s_linkKnown, s_linkUp, s_loaded;
    private static long s_linkDownAt;
    private static long s_onlineSince = -1;
    private static string s_onlineAddress = "";

    private static long Now => new DateTimeOffset(SystemClock.UtcNow).ToUnixTimeSeconds();
    private static long Millis => Stopwatch.GetTimestamp() / Math.Max(1, Stopwatch.Frequency / 1000);

    // ---------- Sampling (kernel loop; cheap).
    public static void Tick()
    {
        long now = Millis;
        if (s_lastSample != long.MinValue && now - s_lastSample < 1000) { return; }
        long bytesIn = EthernetIngress.Bytes, bytesOut = EthernetEgress.Bytes;
        lock (s_gate)
        {
            if (s_lastSample != long.MinValue)
            {
                long elapsed = Math.Max(1, now - s_lastSample);
                s_rateIn = Clamp((bytesIn - s_bytesIn) * 1000 / elapsed); s_rateOut = Clamp((bytesOut - s_bytesOut) * 1000 / elapsed);
                History.Sample(s_rateIn, s_rateOut);
            }
            s_lastSample = now; s_bytesIn = bytesIn; s_bytesOut = bytesOut;
        }
        var adapter = NetworkManager.Primary;
        bool up = adapter.IsValid && adapter.LinkUp;
        if (!s_linkKnown) { s_linkKnown = true; s_linkUp = up; return; }
        if (up == s_linkUp) { return; }
        s_linkUp = up;
        if (!up) { s_linkDownAt = Now; Record(NetworkEventKind.CableOut, 0, "Cable unplugged"); }
        else { long gone = Math.Max(0, Now - s_linkDownAt); Record(NetworkEventKind.CableIn, (int)Math.Min(gone, int.MaxValue), "Cable plugged in again after " + NetworkHistory.Duration(gone)); }
    }
    /// <summary>The last second's traffic in bytes per second (the taskbar's network center).</summary>
    public static (uint In, uint Out) Rates { get { lock (s_gate) { return (s_rateIn, s_rateOut); } } }
    private static uint Clamp(long v) => v <= 0 ? 0 : v >= uint.MaxValue ? uint.MaxValue : (uint)v;

    // ---------- Events.
    public static void Record(NetworkEventKind kind, int value, string text)
    {
        History.Record(Now, kind, value, text);
        Log.Write("network", "event: " + text);
    }
    /// <summary>NetworkService reports each configuration it brought up; a second success at the same address is a renewal.</summary>
    public static void Online(string address, bool dhcp)
    {
        bool renewal;
        lock (s_gate) { renewal = s_onlineSince >= 0 && s_onlineAddress == address; if (!renewal) { s_onlineSince = Now; s_onlineAddress = address; } }
        if (renewal) { Record(NetworkEventKind.Renewed, 0, dhcp ? "Lease renewed" : "Settings applied again"); }
        else { Record(NetworkEventKind.Connected, 0, "Connected, address " + address); }
    }
    public static void Offline(NetworkEventKind kind, string text)
    {
        bool was; lock (s_gate) { was = s_onlineSince >= 0; s_onlineSince = -1; s_onlineAddress = ""; }
        var last = History.Last; bool repeat = last is { } e && e.Kind == kind && e.Text == text;
        // DHCP retries every 30 seconds while offline: the same failure is recorded once.
        if (was || !repeat) { Record(kind, 0, text); }
    }
    /// <summary>A name lookup took this long; slow ones go into the history at most every ten minutes.</summary>
    public static void DnsTime(long milliseconds)
    {
        if (milliseconds < 1000) { return; }
        long now = Now;
        lock (s_gate) { if (now - s_lastDnsSlow < 600) { return; } s_lastDnsSlow = now; }
        Record(NetworkEventKind.DnsSlow, (int)milliseconds, "DNS slow: an answer took " + (milliseconds >= 10000 ? milliseconds / 1000 + " s" : milliseconds + " ms"));
    }

    // ---------- Persistence: the day and week series and the events, in one bounded text file.
    public static void Start()
    {
        Load();
        new Thread(() =>
        {
            using var root = Zenith.Core.Security.CredentialContext.Enter(Zenith.Core.Security.Credentials.Root);
            while (true)
            {
                Thread.Sleep(60000);
                try { Save(); } catch (Exception e) { Log.Write("network", "history not saved: " + e.Message); }
            }
        }).Start();
    }
    private static void Load()
    {
        try
        {
            string? text = BoundedFile.ReadText(HistoryPath, 65536);
            if (text is not null) { History.Parse(text); }
        }
        catch (Exception e) { Log.Write("network", "history ignored: " + e.Message); }
        lock (s_gate) { s_loaded = true; }
    }
    private static void Save()
    {
        bool loaded; lock (s_gate) { loaded = s_loaded; }
        if (!loaded || !History.Dirty || SystemMounts.Mode != BootMode.Installed) { return; }
        if (!Directory.Exists("/var/lib/zenith")) { Directory.CreateDirectory("/var/lib/zenith"); }
        new Zenith.Core.Configuration.NativeSettingsStore().Replace(HistoryPath, History.Serialize());
    }

    // ---------- Snapshots for call 667.
    public const int StatusBytes = 512;
    /// <summary>Status, version 1 (zenith_network.h): state, mode, flags, addresses, lease, connection time, traffic and text.</summary>
    public static byte[] Status()
    {
        var b = new byte[StatusBytes]; var s = b.AsSpan();
        var adapter = NetworkManager.Primary; var c = NetworkService.Configuration; var ip = adapter.IsValid ? adapter.IPConfig : null;
        bool valid = adapter.IsValid, link = valid && adapter.LinkUp;
        string address = ip?.Address.ToString() ?? "0.0.0.0";
        bool online = NetworkService.Online && address != "0.0.0.0";
        uint state = !valid ? 0u : c.Mode == "off" || NetworkService.Disconnected ? 1u : !link ? 2u : NetworkService.Busy && !online ? 3u : online ? 4u : 5u;
        BinaryPrimitives.WriteUInt32LittleEndian(s, 1); BinaryPrimitives.WriteUInt32LittleEndian(s[4..], state);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], c.Mode == "static" ? 1u : c.Mode == "off" ? 2u : 0u);
        uint flags = (link ? 1u : 0) | (NetworkService.LeaseValid ? 2u : 0) | (NetworkService.Busy ? 8u : 0) | (NetworkService.ResolverOverride ? 16u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], flags);
        Ip(s[16..], address); Ip(s[20..], ip?.SubnetMask.ToString() ?? "0.0.0.0"); Ip(s[24..], ip?.DefaultGateway.ToString() ?? "0.0.0.0");
        string[] dns = NetworkService.Resolvers;
        for (int i = 0; i < Math.Min(4, dns.Length); i++) { Ip(s[(28 + 4 * i)..], dns[i]); }
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], (uint)Math.Min(4, dns.Length));
        if (valid && adapter.MacAddress is not null)
        {
            string[] parts = adapter.MacAddress.ToString().Split(':');
            for (int i = 0; i < 6 && i < parts.Length; i++) { s[48 + i] = Convert.ToByte(parts[i], 16); }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], 0);   // link speed: not reported by the driver yet
        long since; lock (s_gate) { since = s_onlineSince; }
        BinaryPrimitives.WriteInt64LittleEndian(s[64..], online && since >= 0 ? Math.Max(0, Now - since) : -1);
        BinaryPrimitives.WriteInt64LittleEndian(s[72..], NetworkService.LeaseSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(s[80..], NetworkService.RenewSeconds);
        BinaryPrimitives.WriteUInt64LittleEndian(s[88..], (ulong)EthernetIngress.Bytes); BinaryPrimitives.WriteUInt64LittleEndian(s[96..], (ulong)EthernetEgress.Bytes);
        lock (s_gate) { BinaryPrimitives.WriteUInt32LittleEndian(s[104..], s_rateIn); BinaryPrimitives.WriteUInt32LittleEndian(s[108..], s_rateOut); }
        BinaryPrimitives.WriteUInt32LittleEndian(s[112..], History.EventTotal);
        Text(s.Slice(116, 16), valid ? "eth0" : "");
        Text(s.Slice(132, 64), NetworkService.Hostname);
        Text(s.Slice(196, 96), NetworkService.State);
        Ip(s[292..], c.Address); Ip(s[296..], c.Mask); Ip(s[300..], c.Gateway); Ip(s[304..], c.Dns);
        BinaryPrimitives.WriteUInt32LittleEndian(s[308..], 0);   // Wi-Fi: no adapter driver
        BinaryPrimitives.WriteUInt32LittleEndian(s[312..], 0);   // VPN: no service
        Text(s.Slice(316, 160), valid ? adapter.Name ?? "" : "");
        var utc = SystemClock.UtcNow;
        BinaryPrimitives.WriteInt32LittleEndian(s[476..], (int)(SystemClock.ToLocal(utc) - utc).TotalSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(s[480..], new DateTimeOffset(utc).ToUnixTimeSeconds());
        return b;
    }
    private static void Ip(Span<byte> s, string text)
    {
        try { uint v = NetworkText.Ip(text); s[0] = (byte)(v >> 24); s[1] = (byte)(v >> 16); s[2] = (byte)(v >> 8); s[3] = (byte)v; }
        catch (Exception) { s[..4].Clear(); }
    }
    private static void Text(Span<byte> s, string text) => NetworkHistory.Text(s, text);

    // ---------- Diagnostics: each step from the adapter outward (Beacon calls them one at a time).
    public enum Result : uint { Ok = 0, Slow = 1, Failed = 2, Skipped = 3 }
    public const int CheckBytes = 176;
    /// <summary>Step 1 adapter, 2 address, 3 gateway, 4 DNS, 5 internet, 6 secure web. Result: version, step, result,
    /// milliseconds, subject (32 bytes) and a plain-words detail (128 bytes).</summary>
    public static byte[] Check(int step, Action cancel)
    {
        var adapter = NetworkManager.Primary; var ip = adapter.IsValid ? adapter.IPConfig : null;
        string address = ip?.Address.ToString() ?? "0.0.0.0", gateway = ip?.DefaultGateway.ToString() ?? "0.0.0.0";
        string? configured = MirrorHost();
        string mirror = configured ?? "1.1.1.1", name = configured is not null && !IsAddress(configured) ? configured : "one.one.one.one";
        Result r = Result.Ok; long ms = 0; string subject = "", detail = "";
        var timer = Stopwatch.StartNew();
        try
        {
            switch (step)
            {
                case 1:
                    subject = adapter.IsValid ? "eth0" : "none";
                    if (!adapter.IsValid) { r = Result.Failed; detail = "No supported network adapter. Add an Intel E1000E adapter to the VM."; }
                    else if (!adapter.LinkUp) { r = Result.Failed; detail = "The cable is out (no link). Connect the cable or the VM's network."; }
                    else { detail = "cable in"; }
                    break;
                case 2:
                    subject = address;
                    if (NetworkService.Configuration.Mode == "off" || NetworkService.Disconnected) { r = Result.Failed; detail = "The connection is turned off. Connect to get an address."; }
                    else if (address == "0.0.0.0" || !NetworkService.Online) { r = Result.Failed; detail = NetworkService.Configuration.Mode == "dhcp" ? "No address from DHCP. The network may have no DHCP server; try manual settings." : "The manual address is not active."; }
                    else { detail = address + (NetworkService.Configuration.Mode == "dhcp" ? " from DHCP" : " set manually"); }
                    break;
                case 3:
                    subject = gateway;
                    if (gateway == "0.0.0.0") { r = Result.Skipped; detail = "No gateway: only the local network is reachable."; break; }
                    try { ms = NetworkService.Ping(gateway, cancel); detail = "answers"; }
                    catch (Exception e) when (e is not OperationCanceledException) { r = Result.Failed; detail = "The gateway does not answer. Check the gateway address or the router."; }
                    break;
                case 4:
                    subject = NetworkService.Resolvers.Length > 0 ? NetworkService.Resolvers[0] : "none";
                    if (NetworkService.Resolvers.Length == 0) { r = Result.Failed; detail = "No DNS server. Names cannot be looked up; add one in the connection settings."; break; }
                    try
                    {
                        subject = name; NetworkService.Resolve(name, cancel); ms = timer.ElapsedMilliseconds;
                        if (ms >= 200) { r = Result.Slow; detail = "DNS is slower than usual. Pages may open late, though everything still works."; }
                        else { detail = "answers"; }
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { r = Result.Failed; detail = "The DNS server does not answer. Try another server, such as the gateway or 1.1.1.1."; }
                    break;
                case 5:
                    subject = mirror;
                    try { NetworkService.Probe(mirror, 443, cancel); ms = timer.ElapsedMilliseconds; detail = "reachable"; }
                    catch (Exception e) when (e is not OperationCanceledException) { r = Result.Failed; detail = "The internet is not reachable from here, though the local network works."; }
                    break;
                case 6:
                    subject = mirror;
                    if (configured is null) { r = Result.Skipped; detail = "No package mirror is set, so there is no trusted server to check."; break; }
                    try
                    {
                        using (var connection = NetworkService.OpenClient(mirror, 443, true, cancel)) { }
                        ms = timer.ElapsedMilliseconds; detail = "verified";
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    { r = Result.Failed; detail = "Secure connections fail: " + (e.Message.Length > 80 ? e.Message[..80] : e.Message); }
                    break;
                default: throw new ArgumentException("step");
            }
        }
        catch (IOException e) { r = Result.Failed; detail = e.Message.Length > 120 ? e.Message[..120] : e.Message; }
        if (ms == 0 && r == Result.Ok && step >= 3) { ms = Math.Max(1, timer.ElapsedMilliseconds); }
        if (step == 4 && r != Result.Failed) { DnsTime(ms); }
        var b = new byte[CheckBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)step);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)r); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)Math.Min(ms, uint.MaxValue));
        Text(b.AsSpan(16, 32), subject); Text(b.AsSpan(48, 128), detail);
        return b;
    }
    /// <summary>The package mirror's host (where updates come from: its TLS certificate is one Zenith trusts), or null.</summary>
    public static string? MirrorHost()
    {
        try
        {
            foreach (var repository in Zenith.Core.Packages.NativeRepositories.Repositories)
            { if (repository.Mirror is string url && url.Length > 0) { return HttpTarget.Parse(url).Host; } }
        }
        catch (Exception) { }
        return null;
    }
    private static bool IsAddress(string host) { try { NetworkText.Ip(host); return true; } catch (Exception) { return false; } }

    // ---------- Changes (root, or the org.zenith.network.manage grant).
    /// <summary>Applies new settings; when they do not bring the connection up (DHCP without an answer, a manual gateway
    /// that does not answer), the previous settings return and stay. Returns the message to show.</summary>
    public static string Apply(NetworkConfiguration next, Action cancel)
    {
        Zenith.Core.Security.GuardedFilesystem.RequireRoot();
        var previous = NetworkService.Configuration;
        next.Validate();
        // Cosmos's NativeAOT exception helper cannot throw a new exception from inside a catch handler (it loops until
        // the kernel halts): only remember the failure there, and revert and report after the handler has ended.
        string? reason = null;
        try
        {
            NetworkService.Save(next, cancel);
            if (next.Mode == "static" && next.Gateway != "0.0.0.0") { NetworkService.Ping(next.Gateway, cancel); }
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException) { throw; }
            reason = next.Mode == "static" ? "the gateway did not answer" : next.Mode == "dhcp" ? "no DHCP answer" : e.Message;
        }
        if (reason is null)
        {
            Record(NetworkEventKind.SettingsChanged, 0, next.Mode == "dhcp" ? "Settings changed: automatic (DHCP)" : next.Mode == "off" ? "Settings changed: turned off" : "Settings changed: manual " + next.Address);
            return "applied";
        }
        bool restored = true;
        try { NetworkService.Save(previous, () => { }); } catch (Exception) { restored = false; }
        Record(NetworkEventKind.SettingsKept, 0, "Kept the previous settings: " + reason);
        throw new IOException("The new settings did not connect (" + reason + ")" + (restored ? "; the previous settings are back." : "; the previous settings are saved but did not connect either."));
    }
}
