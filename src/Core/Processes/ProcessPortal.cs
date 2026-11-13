using System;
using System.Buffers.Binary;
using System.Text;
using Zenith.Core.Portal;

namespace Zenith.Core.Processes;

/// <summary>zenith-native-portal-abi 1.0 (userland/include/zenith_portal.h) over <see cref="PortalBroker"/>:
/// 650 open (256-byte request), 651 poll (handle, 256-byte response), 652 close (handle),
/// 653 info (1024-byte list of interfaces).</summary>
internal static class ProcessPortal
{
    public const int RequestBytes = 256, ResponseBytes = 256, InfoBytes = 1024, InfoEntries = 14;

    // Request: version 0, flags 4 (0), interface[64] 8, method[32] 72, payload_bytes 104, reserved 108, payload[144] 112.
    public static long Open(ReadOnlySpan<byte> b, PortalCaller caller)
    {
        if (b.Length != RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(b[108..]) != 0) { return -22; }
        string? name = Field(b.Slice(8, 64)), method = Field(b.Slice(72, 32));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(b[104..]);
        if (name is null || method is null || length > PortalBroker.MaximumPayload) { return -22; }
        foreach (byte value in b.Slice(112 + (int)length, PortalBroker.MaximumPayload - (int)length)) { if (value != 0) { return -22; } }
        return PortalBroker.Open(caller, name, method, b.Slice(112, (int)length).ToArray());
    }

    // Response: version 0, state 4, handle 8, response_bytes 16, reserved 20, message[96] 24, response[136] 120.
    public static byte[] Poll(PortalRequest request)
    {
        byte[] data = new byte[ResponseBytes]; Span<byte> b = data;
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)request.State);
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], request.Handle);
        BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)request.Response.Length);
        Text(b.Slice(24, 96), request.Message);
        request.Response.CopyTo(b[120..]);
        return data;
    }

    // Info: version 0, count 4, flags 8 (1 = a graphical frontend is attached), reserved 12,
    // then 14 entries of { name[64], version, flags (1 = the frontend answers it) }.
    public static byte[] Info()
    {
        byte[] data = new byte[InfoBytes]; Span<byte> b = data;
        var list = PortalBroker.Interfaces(); int count = Math.Min(list.Count, InfoEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], PortalBroker.HasFrontend ? 1u : 0u);
        for (int i = 0; i < count; i++)
        {
            Span<byte> e = b.Slice(16 + i * 72, 72);
            Text(e[..64], list[i].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(e[64..], list[i].Version);
            BinaryPrimitives.WriteUInt32LittleEndian(e[68..], list[i].Answered ? 1u : 0u);
        }
        return data;
    }

    private static void Text(Span<byte> field, string text)
    {
        int n = 0;
        foreach (char c in text) { if (n == field.Length - 1) { break; } field[n++] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?'; }
    }
    private static string? Field(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0); if (end <= 0) { return null; }
        foreach (byte b in field[end..]) { if (b != 0) { return null; } }
        foreach (byte b in field[..end]) { if (b < 33 || b > 126) { return null; } }
        return Encoding.ASCII.GetString(field[..end]);
    }
}
