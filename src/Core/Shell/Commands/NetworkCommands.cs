using System;
using System.IO;
using Zenith.Core.Network;

namespace Zenith.Core.Shell.Commands;

internal static class NetworkCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("ip", "ip [addr | route]", "Show IPv4 interfaces, routes and service status", Status));
        add(new Command("ifconfig", "ifconfig", "Show IPv4 interfaces and service status", Status));
        add(new Command("netcfg", "netcfg [status | apply | dhcp | off | static IP MASK GATEWAY DNS | dns auto/IP... | ntp HOST [PORT]]", "Configure and save the single IPv4 uplink (root)", Configure));
        add(new Command("nslookup", "nslookup HOST [SERVER [PORT]]", "Resolve hosts or query a specified DNS server", c =>
        {
            if (c.Args.Length is < 1 or > 3) { return c.Fail("specify HOST [SERVER [PORT]]"); }
            string ip = NetworkService.Resolve(c.Args[0], c.Shell.ThrowIfCancelled, c.Args.Length > 1 ? c.Args[1] : null, Port(c.Args, 2, 53));
            c.WriteLine(c.Args[0] + " address=" + ip); return 0;
        }));
        add(new Command("ping", "ping HOST", "Send one correlated ICMP echo request", c =>
        {
            if (c.Args.Length != 1) { return c.Fail("specify one host"); }
            c.WriteLine("reply from " + c.Args[0] + " time=" + NetworkService.Ping(c.Args[0], c.Shell.ThrowIfCancelled) + "ms"); return 0;
        }));
        add(new Command("nc", "nc -z HOST PORT", "Bounded TCP connection probe", c =>
        {
            if (c.Args.Length != 3 || c.Args[0] != "-z") { return c.Fail("this slice supports nc -z HOST PORT"); }
            NetworkService.Probe(c.Args[1], Port(c.Args, 2, 0), c.Shell.ThrowIfCancelled); c.WriteLine("TCP connected " + c.Args[1] + ":" + c.Args[2]); return 0;
        }));
        add(new Command("ntp", "ntp HOST [PORT]", "Correct software UTC with unauthenticated NTP (root)", c =>
        {
            if (c.Args.Length is < 1 or > 2) { return c.Fail("specify HOST [PORT]"); }
            NetworkService.Synchronize(c.Args[0], Port(c.Args, 1, 123), c.Shell.ThrowIfCancelled); c.Write(NetworkService.Status()); return 0;
        }));
        add(new Command("tls", "tls [selftest | trust FILE SHA256]", "Install a fingerprint-checked PEM CA bundle (root)", c =>
        {
            if (c.Args.Length == 1 && c.Args[0] == "selftest")
            { NetworkService.TlsSelfTest(); c.WriteLine("TLS AES/GCM/SHA/CTR-DRBG known-answer tests passed"); return 0; }
            if (c.Args.Length != 3 || c.Args[0] != "trust") { return c.Fail("use tls trust FILE SHA256 (hash of exact PEM file from a trusted channel)"); }
            NetworkService.Trust(c.Resolve(c.Args[1]), c.Args[2], c.Shell.ThrowIfCancelled);
            c.WriteLine("TLS trust saved to /etc/zenith/tls-ca.pem"); return 0;
        }));
        add(new Command("fetch", "fetch [--replace] [--kernel] URL FILE", "Bounded staged HTTP/verified HTTPS download; no package trust", c =>
        {
            bool replace = false, kernel = false; int p = 0;
            while (p < c.Args.Length && c.Args[p].StartsWith("--", StringComparison.Ordinal))
            {
                if (c.Args[p] == "--replace" && !replace) { replace = true; }
                else if (c.Args[p] == "--kernel" && !kernel) { kernel = true; }
                else { return c.Fail("unknown or repeated fetch option"); }
                p++;
            }
            if (c.Args.Length != p + 2) { return c.Fail("specify [--replace] [--kernel] URL FILE"); }
            var progress = new CommandProgress(c.Out, c.IsTerminal); int bytes;
            try { bytes = NetworkService.Fetch(c.Args[p], c.Resolve(c.Args[p + 1]), replace, c.Shell.ThrowIfCancelled, progress.Report, kernel ? Zenith.Core.Boot.KernelPackage.MaximumBytes : HttpDownload.DefaultMaximumBytes); }
            catch { progress.Finish(); throw; }
            progress.Finish();
            c.WriteLine("fetch complete bytes=" + bytes + (c.Args[p].StartsWith("https://", StringComparison.Ordinal) ? " (verified TLS 1.2)" : " (plain HTTP; unauthenticated)")); return 0;
        }));
    }
    private static int Port(string[] args, int index, int fallback)
    {
        int port = index < args.Length ? int.Parse(args[index]) : fallback;
        if (port is < 1 or > 65535) { throw new IOException("port must be 1..65535"); } return port;
    }
    private static int Status(CommandContext c)
    {
        if (c.Args.Length > 1 || (c.Args.Length == 1 && !(c.Args[0] is "addr" or "route"))) { return c.Fail("read-only: ip [addr | route]"); }
        c.Write(NetworkService.Status()); return 0;
    }
    private static int Configure(CommandContext c)
    {
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "status")) { c.Write(NetworkService.Status()); return 0; }
        if (c.Args[0] == "dns")
        {
            if (c.Args.Length is < 2 or > 4) { return c.Fail("use dns auto or dns IP [IP [IP]]"); }
            string[] servers = c.Args.Length == 2 && c.Args[1] == "auto" ? Array.Empty<string>() : c.Args[1..];
            NetworkService.SaveResolvers(servers, c.Shell.ThrowIfCancelled); c.Write(NetworkService.Status()); return 0;
        }
        if (c.Args.Length == 1 && c.Args[0] == "apply") { NetworkService.Apply(c.Shell.ThrowIfCancelled); }
        else
        {
            var next = NetworkService.Configuration;
            if (c.Args.Length == 1 && c.Args[0] is "dhcp" or "off") { next = next with { Mode = c.Args[0] }; }
            else if (c.Args.Length == 5 && c.Args[0] == "static")
            { next = next with { Mode = "static", Address = c.Args[1], Mask = c.Args[2], Gateway = c.Args[3], Dns = c.Args[4] }; }
            else if (c.Args.Length is 2 or 3 && c.Args[0] == "ntp")
            { next = next with { Ntp = c.Args[1] == "off" ? "" : c.Args[1], NtpPort = Port(c.Args, 2, 123) }; }
            else { return c.Fail("use status, apply, dhcp, off, static IP MASK GATEWAY DNS or ntp HOST [PORT]"); }
            NetworkService.Save(next, c.Shell.ThrowIfCancelled);
        }
        c.Write(NetworkService.Status()); return 0;
    }
}
