using System;
using System.IO;
using System.Linq;

namespace Zenith.Core.Network;

internal sealed record NetworkConfiguration(string Mode, string Address = "0.0.0.0", string Mask = "0.0.0.0",
    string Gateway = "0.0.0.0", string Dns = "0.0.0.0", string Ntp = "", int NtpPort = 123)
{
    public const string Path = "/etc/zenith/network.conf";
    public static NetworkConfiguration Default => new("dhcp");
    public string Serialize() => "VERSION=1\nMODE=" + Mode + "\nADDRESS=" + Address + "\nMASK=" + Mask
        + "\nGATEWAY=" + Gateway + "\nDNS=" + Dns + "\nNTP=" + Ntp + "\nNTP_PORT=" + NtpPort + "\n";
    public static NetworkConfiguration Parse(string text)
    {
        if (text.Length > 4096) { throw new IOException("network configuration exceeds 4096 bytes"); }
        var values = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) { continue; }
            int split = line.IndexOf('=');
            if (split < 1 || !values.TryAdd(line.Substring(0, split), line.Substring(split + 1)))
            { throw new IOException("invalid or duplicate network setting"); }
        }
        string Get(string key, string fallback) => values.TryGetValue(key, out string? value) ? value : fallback;
        foreach (string key in values.Keys)
        { if (!(key is "VERSION" or "MODE" or "ADDRESS" or "MASK" or "GATEWAY" or "DNS" or "NTP" or "NTP_PORT")) { throw new IOException("unknown network setting: " + key); } }
        if (Get("VERSION", "") != "1") { throw new IOException("unsupported network configuration version"); }
        var c = new NetworkConfiguration(Get("MODE", ""), Get("ADDRESS", "0.0.0.0"), Get("MASK", "0.0.0.0"),
            Get("GATEWAY", "0.0.0.0"), Get("DNS", "0.0.0.0"), Get("NTP", ""), int.Parse(Get("NTP_PORT", "123")));
        c.Validate(); return c;
    }
    public void Validate()
    {
        if (!(Mode is "dhcp" or "static" or "off")) { throw new IOException("mode must be dhcp, static or off"); }
        uint address = NetworkText.Ip(Address), mask = NetworkText.Ip(Mask), gateway = NetworkText.Ip(Gateway);
        NetworkText.Ip(Dns);
        if (Mode == "static")
        {
            uint inverse = ~mask;
            if (mask == 0 || (inverse & (inverse + 1)) != 0 || inverse < 3 || !NetworkText.Unicast(address)
                || (address & inverse) == 0 || (address & inverse) == inverse)
            { throw new IOException("static address requires a unicast host and contiguous /1../30 mask"); }
            if (gateway != 0 && (!NetworkText.Unicast(gateway) || (gateway & mask) != (address & mask)
                || (gateway & inverse) == 0 || (gateway & inverse) == inverse || gateway == address))
            { throw new IOException("gateway must be a different host in the same subnet (or 0.0.0.0)"); }
        }
        if (Dns != "0.0.0.0" && !NetworkText.Unicast(NetworkText.Ip(Dns))) { throw new IOException("DNS must be unicast"); }
        if (Ntp.Length > 0) { NetworkText.Host(Ntp); }
        if (NtpPort < 1 || NtpPort > 65535) { throw new IOException("invalid NTP port"); }
    }
}

internal static class NetworkText
{
    public static uint Ip(string value)
    {
        string[] parts = value.Split('.'); uint result = 0;
        if (parts.Length != 4) { throw new IOException("invalid IPv4 address: " + value); }
        foreach (string part in parts)
        {
            if (part.Length is < 1 or > 3 || part.Any(c => c < '0' || c > '9') || !byte.TryParse(part, out byte b))
            { throw new IOException("invalid IPv4 address: " + value); }
            result = (result << 8) | b;
        }
        return result;
    }
    public static string Format(uint ip) => (ip >> 24) + "." + ((ip >> 16) & 255) + "." + ((ip >> 8) & 255) + "." + (ip & 255);
    public static bool Unicast(uint ip) => (ip >> 24) is > 0 and < 224 && (ip >> 24) != 127;
    public static string Host(string name)
    {
        if (name.Length is < 1 or > 253) { throw new IOException("invalid host name"); }
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-')))
            { throw new IOException("invalid host name: " + name); }
        }
        return name.TrimEnd('.').ToLowerInvariant();
    }
    public static string? LookupHosts(string text, string name)
    {
        if (text.Length > 16384) { throw new IOException("hosts file exceeds limit"); }
        foreach (string raw in text.Split('\n'))
        {
            string[] fields = raw.Split('#')[0].Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) { continue; }
            for (int i = 1; i < fields.Length; i++)
            { if (fields[i].Equals(name, StringComparison.OrdinalIgnoreCase)) { return Format(Ip(fields[0])); } }
        }
        return null;
    }
    public static string[] Resolvers(string text)
    {
        if (text.Length > 16384) { throw new IOException("resolv.conf exceeds limit"); }
        var result = new System.Collections.Generic.List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string[] fields = raw.Split('#')[0].Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0 && fields[0] == "nameserver")
            {
                if (fields.Length != 2 || !Unicast(Ip(fields[1]))) { throw new IOException("invalid nameserver"); }
                if (result.Count >= 3) { throw new IOException("at most three nameservers supported"); }
                result.Add(Format(Ip(fields[1])));
            }
        }
        return result.ToArray();
    }
}
