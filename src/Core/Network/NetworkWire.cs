using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Network;

internal static class NetworkWire
{
    public static ushort U16(byte[] b, int p) => (ushort)((b[p] << 8) | b[p + 1]);
    public static uint U32(byte[] b, int p) => ((uint)U16(b, p) << 16) | U16(b, p + 2);
    public static void Put16(byte[] b, int p, ushort n) { b[p] = (byte)(n >> 8); b[p + 1] = (byte)n; }
    public static void Put32(byte[] b, int p, uint n) { Put16(b, p, (ushort)(n >> 16)); Put16(b, p + 2, (ushort)n); }
    public static void Need(byte[] b, int p, int count)
    { if (p < 0 || count < 0 || p > b.Length - count) { throw new IOException("truncated network message"); } }
}

internal sealed record DhcpLease(uint Address, uint Mask, uint Gateway, uint Dns, uint Server, uint Seconds);
internal static class DhcpWire
{
    public static byte[] Request(uint transaction, byte[] mac, DhcpLease? offer = null)
    {
        if (mac.Length != 6) { throw new IOException("expected Ethernet MAC"); }
        byte[] packet = new byte[300]; packet[0] = 1; packet[1] = 1; packet[2] = 6;
        NetworkWire.Put32(packet, 4, transaction); NetworkWire.Put16(packet, 10, 0x8000);
        mac.CopyTo(packet, 28); NetworkWire.Put32(packet, 236, 0x63825363);
        int p = 240; packet[p++] = 53; packet[p++] = 1; packet[p++] = offer is null ? (byte)1 : (byte)3;
        if (offer is not null)
        {
            packet[p++] = 50; packet[p++] = 4; NetworkWire.Put32(packet, p, offer.Address); p += 4;
            packet[p++] = 54; packet[p++] = 4; NetworkWire.Put32(packet, p, offer.Server); p += 4;
        }
        packet[p++] = 55; packet[p++] = 4; packet[p++] = 1; packet[p++] = 3; packet[p++] = 6; packet[p++] = 51;
        packet[p] = 255; return packet;
    }
    public static DhcpLease Parse(byte[] packet, uint transaction, byte[] mac, byte expectedType)
    {
        if (mac.Length != 6 || packet.Length is < 240 or > 1500 || packet[0] != 2 || packet[1] != 1 || packet[2] != 6
            || NetworkWire.U32(packet, 4) != transaction || NetworkWire.U32(packet, 236) != 0x63825363)
        { throw new IOException("unrelated or invalid DHCP reply"); }
        for (int i = 0; i < 6; i++) { if (packet[28 + i] != mac[i]) { throw new IOException("DHCP MAC mismatch"); } }
        var options = new Dictionary<byte, byte[]>(); bool ended = false;
        for (int p = 240; p < packet.Length;)
        {
            byte type = packet[p++]; if (type == 0) { continue; } if (type == 255) { ended = true; break; }
            NetworkWire.Need(packet, p, 1); int length = packet[p++]; NetworkWire.Need(packet, p, length);
            if (!options.TryAdd(type, packet.AsSpan(p, length).ToArray())) { throw new IOException("duplicate DHCP option"); } p += length;
        }
        if (!ended || !options.TryGetValue(53, out byte[]? message) || message.Length != 1 || message[0] != expectedType)
        { throw new IOException("unexpected DHCP message type"); }
        uint Get(byte key, bool required = false, bool list = false)
        {
            if (!options.TryGetValue(key, out var bytes)) { if (required) { throw new IOException("missing DHCP option"); } return 0; }
            if (bytes.Length < 4 || (!list && bytes.Length != 4) || bytes.Length % 4 != 0) { throw new IOException("invalid DHCP option length"); }
            return NetworkWire.U32(bytes, 0);
        }
        var lease = new DhcpLease(NetworkWire.U32(packet, 16), Get(1, true), Get(3, list: true), Get(6, list: true), Get(54, true), Get(51, true));
        new NetworkConfiguration("static", NetworkText.Format(lease.Address), NetworkText.Format(lease.Mask),
            NetworkText.Format(lease.Gateway), NetworkText.Format(lease.Dns)).Validate();
        if (!NetworkText.Unicast(lease.Server) || lease.Seconds < 30) { throw new IOException("invalid DHCP server or lease duration"); }
        return lease;
    }
}

internal static class DnsWire
{
    public static byte[] Query(string name, ushort id)
    {
        name = NetworkText.Host(name); using var stream = new MemoryStream();
        byte[] header = new byte[12]; NetworkWire.Put16(header, 0, id); header[2] = 1; header[5] = 1; stream.Write(header);
        foreach (string label in name.Split('.')) { stream.WriteByte((byte)label.Length); stream.Write(Encoding.ASCII.GetBytes(label)); }
        stream.Write(new byte[] { 0, 0, 1, 0, 1 }); return stream.ToArray();
    }
    public static string Parse(byte[] packet, string name, ushort id)
    {
        NetworkWire.Need(packet, 0, 12);
        if (packet.Length > 512 || NetworkWire.U16(packet, 0) != id || (NetworkWire.U16(packet, 2) & 0xfa0f) != 0x8000
            || NetworkWire.U16(packet, 4) != 1 || NetworkWire.U16(packet, 6) > 32)
        { throw new IOException("DNS reply rejected (ID, flags, truncation, error or count)"); }
        int p = 12; string question = Name(packet, ref p);
        NetworkWire.Need(packet, p, 4);
        if (question != NetworkText.Host(name) || NetworkWire.U16(packet, p) != 1 || NetworkWire.U16(packet, p + 2) != 1)
        { throw new IOException("DNS question mismatch"); } p += 4;
        var addresses = new Dictionary<string, string>(); var aliases = new Dictionary<string, string>();
        int records = NetworkWire.U16(packet, 6);
        for (int i = 0; i < records; i++)
        {
            string owner = Name(packet, ref p); NetworkWire.Need(packet, p, 10);
            ushort type = NetworkWire.U16(packet, p), klass = NetworkWire.U16(packet, p + 2), length = NetworkWire.U16(packet, p + 8); p += 10;
            NetworkWire.Need(packet, p, length);
            if (klass == 1 && type == 1 && length == 4) { addresses.TryAdd(owner, NetworkText.Format(NetworkWire.U32(packet, p))); }
            if (klass == 1 && type == 5)
            {
                int end = p; string target = Name(packet, ref end);
                if (end != p + length) { throw new IOException("invalid CNAME length"); } aliases.TryAdd(owner, target);
            }
            p += length;
        }
        string current = question;
        for (int i = 0; i < 16; i++)
        { if (addresses.TryGetValue(current, out string? address)) { return address; } if (!aliases.TryGetValue(current, out current!)) { break; } }
        throw new IOException("DNS reply contains no matching IPv4 answer");
    }
    private static string Name(byte[] packet, ref int position)
    {
        int p = position, consumed = -1, steps = 0; var name = new StringBuilder();
        while (++steps <= 128)
        {
            NetworkWire.Need(packet, p, 1); byte length = packet[p++];
            if ((length & 0xc0) == 0xc0)
            {
                NetworkWire.Need(packet, p, 1); int target = ((length & 63) << 8) | packet[p++];
                if (target >= p - 2) { throw new IOException("invalid DNS compression pointer"); }
                if (consumed < 0) { consumed = p; } p = target; continue;
            }
            if (length == 0) { position = consumed < 0 ? p : consumed; return NetworkText.Host(name.ToString()); }
            if (length > 63) { throw new IOException("invalid DNS label"); } NetworkWire.Need(packet, p, length);
            if (name.Length > 0) { name.Append('.'); } name.Append(Encoding.ASCII.GetString(packet, p, length)); p += length;
            if (name.Length > 253) { throw new IOException("DNS name exceeds limit"); }
        }
        throw new IOException("DNS compression loop");
    }
}

internal static class NtpWire
{
    private static readonly DateTime Epoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static byte[] Request(byte[] nonce)
    { if (nonce.Length != 8) { throw new IOException("NTP nonce must be eight bytes"); } byte[] packet = new byte[48]; packet[0] = 0x23; nonce.CopyTo(packet, 40); return packet; }
    public static DateTime Parse(byte[] packet, byte[] nonce)
    {
        if (packet.Length != 48 || ((packet[0] >> 3) & 7) is < 3 or > 4 || (packet[0] & 7) != 4
            || (packet[0] >> 6) == 3 || packet[1] is < 1 or > 15)
        { throw new IOException("unsynchronized or invalid NTP reply"); }
        for (int i = 0; i < 8; i++) { if (packet[24 + i] != nonce[i]) { throw new IOException("NTP originate mismatch"); } }
        uint seconds = NetworkWire.U32(packet, 40); ulong eraSeconds = seconds < 0x80000000 ? (1UL << 32) + seconds : seconds;
        DateTime result = Epoch.AddSeconds(eraSeconds).AddTicks((long)((ulong)NetworkWire.U32(packet, 44) * 10000000 >> 32));
        if (result.Year < 2020 || result.Year > 2100) { throw new IOException("NTP time outside supported 2020..2100 range"); }
        return result;
    }
}
