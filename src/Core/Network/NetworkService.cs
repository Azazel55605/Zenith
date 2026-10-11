#pragma warning disable COSMOS0002 // Pin experimental ICMP packet seam to Cosmos 3.0.89; validate in QEMU.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Network;
using Cosmos.Kernel.System.Network.Config;
using Cosmos.Kernel.System.Network.IPv4;
using UdpClient = Cosmos.Kernel.System.Network.UDP.UdpClient;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Configuration;
using Zenith.Core.Security;
using Zenith.Core.Storage;
using Zenith.Core.Time;
using NetPoint = Cosmos.Kernel.System.Network.EndPoint;

namespace Zenith.Core.Network;

/// <summary>Single IPv4 uplink. Serializes operations across the framework's shared client tables.</summary>
internal static class NetworkService
{
    private static readonly object s_gate = new();
    private static bool s_busy;
    private static bool s_invalidConfiguration;
    private static long s_renewAt, s_expiresAt;
    private static string s_state = "not initialized", s_time = "not synchronized (unauthenticated NTP)";
    private static string[] s_dns = Array.Empty<string>();
    public static NetworkConfiguration Configuration { get; private set; } = NetworkConfiguration.Default;
    public static string Hostname { get; private set; } = "zenith";
    private static NetworkAdapter Adapter => NetworkManager.Primary;
    public static void Initialize()
    {
        try
        {
            Hostname = NetworkText.Host((BoundedFile.ReadText("/etc/hostname", 256) ?? "zenith").Trim());
            DnsConfig.HostName = Hostname;
            Configuration = NetworkConfiguration.Parse(BoundedFile.ReadText(NetworkConfiguration.Path, 4096) ?? Configuration.Serialize());
            // Check local files before enabling traffic; preserve invalid/future configuration.
            NetworkText.Resolvers(BoundedFile.ReadText("/etc/resolv.conf", 16384) ?? "");
            NetworkText.LookupHosts(BoundedFile.ReadText("/etc/hosts", 16384) ?? "", "localhost");
        }
        catch (Exception e) { s_invalidConfiguration = true; s_state = "configuration rejected: " + e.Message; Log.Write("network", s_state); return; }
        StartBackground();
    }
    private static void StartBackground()
    {
        lock (s_gate) { if (s_busy) { return; } s_busy = true; }
        new Thread(() =>
        {
            using var credentials = CredentialContext.Enter(Credentials.Root);
            try
            {
                ApplyCore(() => { });
                if (Configuration.Ntp.Length > 0 && Adapter.IPConfig is not null) { SynchronizeCore(Configuration.Ntp, Configuration.NtpPort, () => { }); }
            }
            catch (Exception e) { Log.Write("network", "boot/renewal: " + e.Message); }
            finally { lock (s_gate) { s_busy = false; } }
        }).Start();
    }
    public static void Tick()
    {
        if (!s_invalidConfiguration && Configuration.Mode == "dhcp" && s_renewAt != 0 && Stopwatch.GetTimestamp() >= s_renewAt)
        { StartBackground(); }
    }
    private sealed class Operation : IDisposable
    {
        public void Dispose() { lock (s_gate) { s_busy = false; } }
    }
    private static Operation Enter()
    {
        bool busy;
        lock (s_gate) { busy = s_busy; if (!busy) { s_busy = true; } }
        if (busy) { throw new IOException("network busy; retry after the current operation finishes"); }
        return new Operation();
    }
    // Cosmos 3.0.89 skips unwind-finally handlers. Catch in the owning frame before
    // rethrowing so failed/cancelled operations release registrations and handles too.
    private static T Run<T>(Func<T> body)
    {
        var operation = Enter();
        try { T result = body(); operation.Dispose(); return result; }
        catch { operation.Dispose(); throw; }
    }
    private static void Run(Action body) => Run(() => { body(); return 0; });
    private static void Close(Socket? socket) { try { socket?.Close(0); } catch { } }
    private static void Cleanup(string path) { try { if (File.Exists(path)) { File.Delete(path); } } catch { Log.Write("network", "staging cleanup failed: " + path); } }
    private static byte[] Random(int count)
    {
        byte[] bytes = new byte[count];
        if (!HardwareEntropy.Source.TryFill(bytes)) { throw new IOException("network requires hardware entropy; no predictable fallback"); }
        return bytes;
    }
    private static bool Expired => s_expiresAt != 0 && Stopwatch.GetTimestamp() >= s_expiresAt;
    private static void Ready()
    {
        if (!Adapter.IsValid) { throw new IOException("no supported network adapter"); }
        if (!Adapter.Ready || !Adapter.LinkUp) { throw new IOException("network link is down"); }
        if (Adapter.IPConfig is null || Adapter.IPConfig.Address.ToString() == "0.0.0.0" || Expired)
        { throw new IOException("no usable address; run netcfg apply (root)"); }
    }
    public static string Status()
    {
        var b = new StringBuilder(); b.Append("hostname=" + Hostname + " mode=" + Configuration.Mode + " state=" + s_state + "\n");
        lock (s_gate) { b.Append("operation=" + (s_busy ? "busy" : "idle") + "\n"); }
        for (int i = 0; i < NetworkManager.DeviceCount; i++)
        {
            var a = NetworkManager.GetAdapter(i); var ip = a.IPConfig;
            b.Append("eth" + i + " " + a.Name + " mac=" + a.MacAddress + " link=" + (a.LinkUp ? "up" : "down") + " ready=" + a.Ready + "\n");
            b.Append("  address=" + (ip?.Address.ToString() ?? "none") + " mask=" + (ip?.SubnetMask.ToString() ?? "none")
                + " gateway=" + (ip?.DefaultGateway.ToString() ?? "none") + "\n");
        }
        b.Append("dns=" + string.Join(",", s_dns) + " lease=" + (Expired ? "expired" : s_expiresAt == 0 ? "none/static" : "valid") + "\n");
        b.Append("time=" + s_time + "\n"); return b.ToString();
    }
    public static void Apply(Action cancel)
    {
        GuardedFilesystem.RequireRoot(); Run(() =>
        {
            // Validate every file before changing the active service or address.
            cancel();
            var next = NetworkConfiguration.Parse(BoundedFile.ReadText(NetworkConfiguration.Path, 4096) ?? Configuration.Serialize());
            string hostname = NetworkText.Host((BoundedFile.ReadText("/etc/hostname", 256) ?? Hostname).Trim());
            NetworkText.Resolvers(BoundedFile.ReadText("/etc/resolv.conf", 16384) ?? "");
            NetworkText.LookupHosts(BoundedFile.ReadText("/etc/hosts", 16384) ?? "", "localhost");
            Configuration = next; Hostname = hostname; DnsConfig.HostName = hostname;
            s_invalidConfiguration = false; ApplyCore(cancel);
        });
    }
    public static void SaveResolvers(string[] servers, Action cancel)
    {
        GuardedFilesystem.RequireRoot(); Run(() =>
        {
            cancel();
            var next = NetworkConfiguration.Parse(BoundedFile.ReadText(NetworkConfiguration.Path, 4096) ?? Configuration.Serialize());
            string text = "";
            foreach (string server in servers) { text += "nameserver " + NetworkText.Format(NetworkText.Ip(server)) + "\n"; }
            NetworkText.Resolvers(text);
            new NativeSettingsStore().Replace("/etc/resolv.conf", text);
            Configuration = next; s_invalidConfiguration = false; ApplyCore(cancel);
        });
    }
    public static void Save(NetworkConfiguration configuration, Action cancel)
    {
        GuardedFilesystem.RequireRoot(); Run(() => { cancel(); configuration.Validate();
        new NativeSettingsStore().Replace(NetworkConfiguration.Path, configuration.Serialize());
        Configuration = configuration; s_invalidConfiguration = false; ApplyCore(cancel); });
    }
    private static void Clear()
    {
        NetworkStack.RemoveAllConfigIP(); s_dns = Array.Empty<string>(); s_expiresAt = 0;
        foreach (var server in new System.Collections.Generic.List<Address>(DnsConfig.Nameservers)) { DnsConfig.Remove(server); }
    }
    private static void ApplyCore(Action cancel)
    {
        s_state = "configuring"; s_renewAt = 0; Clear(); UdpClient? dhcp = null;
        try
        {
            cancel();
            if (Configuration.Mode == "off") { s_state = "disabled"; return; }
            if (!Adapter.IsValid || !Adapter.Ready || !Adapter.LinkUp) { throw new IOException("no ready adapter/link"); }
            if (Configuration.Mode == "static")
            {
                Configure(Configuration.Address, Configuration.Mask, Configuration.Gateway);
                if (Configuration.Dns != "0.0.0.0") { s_dns = new[] { Configuration.Dns }; }
            }
            else
            {
                Configure("0.0.0.0", "0.0.0.0", "0.0.0.0");
                byte[] mac = new byte[6]; string[] segments = Adapter.MacAddress!.ToString().Split(':');
                for (int i = 0; i < 6; i++) { mac[i] = Convert.ToByte(segments[i], 16); }
                uint id = NetworkWire.U32(Random(4), 0);
                using var client = new UdpClient(68); dhcp = client; client.Connect(Address4.Broadcast, 67);
                DhcpLease? offer = null;
                for (int phase = 0; phase < 2; phase++)
                {
                    byte[] request = DhcpWire.Request(id, mac, offer); bool received = false;
                    for (int attempt = 0; attempt < 2 && !received; attempt++)
                    {
                        cancel(); client.Send(request); var timer = Stopwatch.StartNew();
                        while (timer.ElapsedMilliseconds < 2000)
                        {
                            cancel(); if (!Adapter.LinkUp) { throw new IOException("link disconnected during DHCP"); }
                            var source = new NetPoint(Address4.Zero, 0); byte[]? reply = client.Receive(ref source, 0);
                            if (reply is null) { Thread.Sleep(10); continue; }
                            if (source.Port != 67) { continue; }
                            try
                            {
                                var lease = DhcpWire.Parse(reply, id, mac, phase == 0 ? (byte)2 : (byte)5);
                                if (phase == 1 && (lease.Address != offer!.Address || lease.Server != offer.Server)) { continue; }
                                offer = lease; received = true; break;
                            }
                            catch (Exception e) { if (e is not IOException) { throw; } }
                        }
                    }
                    if (!received) { throw new IOException("DHCP timed out; no accepted " + (phase == 0 ? "offer" : "ACK")); }
                }
                Configure(NetworkText.Format(offer!.Address), NetworkText.Format(offer.Mask), NetworkText.Format(offer.Gateway));
                if (offer.Dns != 0) { s_dns = new[] { NetworkText.Format(offer.Dns) }; }
                long duration = Math.Min((long)offer.Seconds, 86400) * Stopwatch.Frequency;
                s_expiresAt = Stopwatch.GetTimestamp() + duration; s_renewAt = Stopwatch.GetTimestamp() + duration / 2;
            }
            string[] overrides = NetworkText.Resolvers(BoundedFile.ReadText("/etc/resolv.conf", 16384) ?? "");
            if (overrides.Length > 0) { s_dns = overrides; }
            foreach (string dns in s_dns) { DnsConfig.Add(new Address4(NetworkText.Ip(dns))); }
            s_state = "online"; Log.Write("network", "online address=" + Adapter.IPConfig!.Address + " dns=" + string.Join(",", s_dns));
        }
        catch
        {
            dhcp?.Dispose(); Clear(); s_state = "offline (configuration failed/cancelled)";
            if (Configuration.Mode == "dhcp") { s_renewAt = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency; }
            throw;
        }
    }
    private static void Configure(string address, string mask, string gateway)
    {
        NetworkStack.RemoveAllConfigIP();
        if (!IPConfig.Enable(Adapter, new Address4(NetworkText.Ip(address)), new Address4(NetworkText.Ip(mask)), new Address4(NetworkText.Ip(gateway))))
        { throw new IOException("adapter refused IPv4 configuration"); }
    }
    private static byte[] Exchange(byte[] request, string address, int port, Action cancel, Func<byte[], bool> accept)
    {
        Ready(); int local = UdpClient.GetDynamicPort(); if (local == 0) { throw new IOException("no free UDP port"); }
        using var client = new UdpClient(local);
        try
        { client.Connect(new Address4(NetworkText.Ip(address)), port);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancel(); client.Send(request); var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 2000)
            {
                cancel(); Ready(); var source = new NetPoint(Address4.Zero, 0); byte[]? reply = client.Receive(ref source, 0);
                if (reply is null) { Thread.Sleep(10); continue; }
                if (source.Port == port && source.Address.ToString() == NetworkText.Format(NetworkText.Ip(address)) && accept(reply)) { return reply; }
            }
        }
        throw new IOException("UDP request timed out (server, route or firewall)");
        } catch { client.Dispose(); throw; }
    }
    private static string ResolveCore(string host, Action cancel, string? server = null, int port = 53)
    {
        cancel(); try { return NetworkText.Format(NetworkText.Ip(host)); } catch (Exception e) { if (e is not IOException) { throw; } }
        host = NetworkText.Host(host);
        if (server is null)
        {
            string? local = NetworkText.LookupHosts(BoundedFile.ReadText("/etc/hosts", 16384) ?? "", host);
            if (local is not null) { return local; }
        }
        Ready(); string[] servers = server is null ? s_dns : new[] { server };
        if (servers.Length == 0) { throw new IOException("no DNS servers; configure resolv.conf or DHCP"); }
        ushort id = NetworkWire.U16(Random(2), 0); byte[] query = DnsWire.Query(host, id); string answer = "";
        foreach (string dns in servers)
        {
            try
            {
                Exchange(query, dns, port, cancel, reply =>
                { try { answer = DnsWire.Parse(reply, host, id); return true; } catch (Exception e) { if (e is not IOException) { throw; } return false; } });
                return answer;
            }
            catch (Exception e) { if (e is not IOException) { throw; } }
        }
        throw new IOException("DNS resolution failed: timeout or rejected reply");
    }
    public static string Resolve(string host, Action cancel, string? server = null, int port = 53)
    { return Run(() => ResolveCore(host, cancel, server, port)); }
    public static long Ping(string host, Action cancel)
    {
        return Run(() => { string ip = ResolveCore(host, cancel); Ready();
        using var client = new IcmpClient();
        try { client.Connect(new Address4(NetworkText.Ip(ip)));
        ushort id = NetworkWire.U16(Random(2), 0); var timer = Stopwatch.StartNew(); client.SendEcho(id, 1);
        while (timer.ElapsedMilliseconds < 3000)
        {
            cancel(); Ready(); var packet = client.ReceivePacket(0);
            if (packet is not null && packet.IcmpType == 0 && packet.SourceIP.ToString() == ip)
            { var echo = new IcmpEchoReply(packet.RawData); if (echo.IcmpId == id && echo.IcmpSequence == 1) { return timer.ElapsedMilliseconds; } }
            Thread.Sleep(10);
        }
        throw new IOException("ICMP echo timed out (destination may reject ping)");
        } catch { client.Dispose(); throw; } });
    }
    public static void Synchronize(string host, int port, Action cancel)
    { GuardedFilesystem.RequireRoot(); Run(() => SynchronizeCore(host, port, cancel)); }
    private static void SynchronizeCore(string host, int port, Action cancel)
    {
        byte[] nonce = Random(8); DateTime time = default; var timer = Stopwatch.StartNew();
        Exchange(NtpWire.Request(nonce), ResolveCore(host, cancel), port, cancel, reply =>
        { try { time = NtpWire.Parse(reply, nonce); return true; } catch (Exception e) { if (e is not IOException) { throw; } return false; } });
        cancel(); SystemClock.Synchronize(time); s_time = "synchronized via " + host + " (unauthenticated; RTT=" + timer.ElapsedMilliseconds + "ms)";
        Log.Write("network", s_time);
    }
    private static Socket ConnectCore(string host, int port, Action cancel)
    {
        string ip = ResolveCore(host, cancel); Ready(); cancel();
        var socket = new Socket(System.Net.Sockets.AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try { socket.Connect(new IPAddress(new Address4(NetworkText.Ip(ip)).ToBytes()), port); cancel(); return socket; }
        catch { socket.Close(0); throw; }
    }
    public static void Probe(string host, int port, Action cancel)
    { Run(() => { Socket socket = ConnectCore(host, port, cancel); Close(socket); }); }
    public static void TlsSelfTest() => Run(() =>
    {
        TlsConnection.SelfTest();
        // Exercise the patched ingress entry, not just the host-tested validator.
        NetworkStack.HandlePacket(new byte[1], 14);
        NetworkStack.HandlePacket(new byte[42], 1515);
        byte[] broken = new byte[42]; broken[12] = 8; broken[14] = 0x45;
        broken[16] = 255; broken[17] = 255;
        NetworkStack.HandlePacket(broken, broken.Length);
    });
    public static void Trust(string path, string fingerprint, Action cancel)
    {
        GuardedFilesystem.RequireRoot(); Run(() =>
        {
            cancel(); string ca = BoundedFile.ReadText(path, 4096) ?? throw new IOException("trust source does not exist");
            foreach (char c in ca) { if (c > 127 || c == 0) { throw new IOException("trust bundle must be ASCII PEM"); } }
            if (fingerprint.Length != 64) { throw new IOException("expected SHA256 of the exact PEM file (64 hex digits)"); }
            byte[] hash = new byte[32]; Sha256.Hash(Encoding.ASCII.GetBytes(ca), hash);
            if (!Convert.ToHexString(hash).Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("trust bundle SHA256 mismatch; existing trust preserved"); }
            TlsConnection.ValidateTrust(ca); cancel();
            new NativeSettingsStore().Replace(TlsConnection.TrustPath, ca);
        });
    }
    public static int Fetch(string url, string destination, bool replace, Action cancel, Action<string> report)
    {
        return Run(() => { var target = HttpTarget.Parse(url); var metadata = new VfsFileMetadata();
        if (metadata.TryRead(destination, out var existing) && (!replace || existing.Type != "regular file"))
        { throw new IOException("destination exists; --replace permits regular files only"); }
        string temporary = destination + ".fetch-" + Convert.ToHexString(Random(8));
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancel(); Socket? socket = null; TlsConnection? tls = null; IVfsFileHandle? staged = null; bool retry = false;
                try
                {
                    if (File.Exists(temporary)) { File.Delete(temporary); }
                    if (!VfsManager.TryCreateFile(temporary, (VfsMode)0x180) || !VfsManager.TryOpenFile(temporary, out var file))
                    { throw new IOException("cannot stage download beside destination"); }
                    staged = file; int received;
                    using (file)
                    {
                        var decoder = new HttpDownload((bytes, offset, count) =>
                        { if (file.Write(bytes.AsSpan(offset, count)) != count) { throw new IOException("download write failed"); } }, cancel);
                        socket = ConnectCore(target.Host, target.Port, cancel);
                        byte[] request = target.Request;
                        if (target.Secure)
                        {
                            string ca = BoundedFile.ReadText(TlsConnection.TrustPath, 4096) ?? throw new IOException("no TLS trust bundle; root must run tls trust FILE SHA256");
                            tls = new TlsConnection(socket, target.Host, ca, cancel, Ready); tls.Write(request);
                        }
                        else if (socket.Send(request, 0, request.Length, SocketFlags.None) != request.Length) { throw new IOException("short HTTP request write"); }
                        var total = Stopwatch.StartNew(); var idle = Stopwatch.StartNew(); int nextProgress = 65536;
                        while (!decoder.Done)
                        {
                            cancel(); Ready(); int available = socket.Available;
                            if (tls is not null)
                            {
                                byte[] data = new byte[16384]; int count = tls.Read(data);
                                if (count > 0) { decoder.Feed(data, count); idle.Restart(); }
                                else { decoder.Complete(); }
                                if (decoder.Received >= nextProgress) { report("fetch progress " + decoder.Received + "/" + decoder.Length); nextProgress = decoder.Received + 65536; }
                            }
                            else if (available > 0)
                            {
                                // Transport adaptation caps pending data and preserves snapshots across IRQ appends.
                                // Drain currently available data; file size never determines receive allocation.
                                if (available > 131072) { throw new IOException("TCP receive staging exceeds 128 KiB"); }
                                byte[] data = new byte[available]; int count = socket.Receive(data, 0, data.Length, SocketFlags.None);
                                if (count > 0) { decoder.Feed(data, count); idle.Restart(); }
                                if (decoder.Received >= nextProgress) { report("fetch progress " + decoder.Received + "/" + decoder.Length); nextProgress = decoder.Received + 65536; }
                            }
                            else if (!socket.Connected) { decoder.Complete(); }
                            if (idle.ElapsedMilliseconds > 3000 || total.ElapsedMilliseconds > 30000) { throw new IOException("HTTP transfer timed out"); }
                            if (!decoder.Done) { Thread.Sleep(10); }
                        }
                        decoder.Complete(); cancel(); if (!file.TryFlush()) { throw new IOException("download flush failed"); } received = decoder.Received;
                    }
                    cancel();
                    if (metadata.TryRead(destination, out existing) && (!replace || existing.Type != "regular file")) { throw new IOException("destination changed during download"); }
                    if (!VfsManager.TryRename(temporary, destination)) { throw new IOException("download commit failed"); }
                    SystemMounts.SyncAll(); return received;
                }
                // Use one catch-all: the released runtime does not reliably select typed catches.
                // Do not invoke cancellation/output callbacks from inside its catch funclet.
                catch (Exception e)
                {
                    staged?.Dispose(); tls?.Dispose(); Close(socket);
                    if (e is not IOException || attempt != 0) { throw; }
                    retry = true;
                }
                finally { tls?.Dispose(); Close(socket); }
                if (retry) { cancel(); report("fetch retry 2/2"); }
            }
            throw new IOException("HTTP download failed");
        }
        catch { Cleanup(temporary); throw; }
        finally { Cleanup(temporary); } });
    }
}
