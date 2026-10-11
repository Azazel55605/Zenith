using System;

namespace Zenith.Core.Network.Transport;

/// <summary>Validate the supported Ethernet/IPv4 profile before framework parsers enter IRQ handlers.</summary>
public static class EthernetGuard
{
    public static bool Accept(byte[]? frame, int length)
    {
        if (frame is null || length < 14 || length > 1514 || length > frame.Length) { return false; }
        int type = U16(frame, 12);
        if (type == 0x0806)
        {
            return length >= 42 && U16(frame, 14) == 1 && U16(frame, 16) == 0x0800
                && frame[18] == 6 && frame[19] == 4 && U16(frame, 20) is 1 or 2;
        }
        if (type != 0x0800 || length < 34 || frame[14] >> 4 != 4) { return false; }
        int header = (frame[14] & 15) * 4, total = U16(frame, 16);
        if (header < 20 || total < header || total > length - 14 || header > length - 14
            || (U16(frame, 20) & 0xbfff) != 0 || frame[22] == 0) { return false; }
        // Fragment reassembly is unavailable. Validate IPv4 options without trusting their lengths.
        if (!Options(frame, 34, 14 + header, false) || !Checksum(frame, 14, header)) { return false; }
        int start = 14 + header, payload = total - header;
        switch (frame[23])
        {
            case 1: return payload >= 8 && Checksum(frame, start, payload);
            case 17: return payload >= 8 && U16(frame, start + 4) == payload;
            case 6:
                if (payload < 20) { return false; }
                int tcpHeader = (frame[start + 12] >> 4) * 4;
                return tcpHeader >= 20 && tcpHeader <= payload && Options(frame, start + 20, start + tcpHeader, true);
            default: return false;
        }
    }
    private static bool Options(byte[] bytes, int position, int end, bool tcp)
    {
        while (position < end)
        {
            int kind = bytes[position++];
            if (kind == 0) { return true; }
            if (kind == 1) { continue; }
            if (position == end) { return false; }
            int length = bytes[position++];
            if (length < 2 || length - 2 > end - position) { return false; }
            if (tcp && ((kind == 2 && length != 4) || (kind == 3 && length != 3)
                || (kind == 4 && length != 2) || (kind == 5 && (length < 10 || (length - 2) % 8 != 0))
                || (kind == 8 && length != 10))) { return false; }
            position += length - 2;
        }
        return true;
    }
    private static int U16(byte[] bytes, int p) => (bytes[p] << 8) | bytes[p + 1];
    private static bool Checksum(byte[] bytes, int offset, int length)
    {
        uint sum = 0; int end = offset + length;
        while (offset + 1 < end) { sum += (uint)U16(bytes, offset); offset += 2; }
        if (offset < end) { sum += (uint)bytes[offset] << 8; }
        while (sum >> 16 != 0) { sum = (sum & 65535) + (sum >> 16); }
        return sum == 65535;
    }
}
