using System;
using System.Buffers.Binary;
using System.Text;
namespace Zenith.Core.Processes;

internal static class ProcessSettingsInformation
{
    public const int Bytes = 4096;
    public static byte[] Encode(uint kind, string text)
    {
        byte[] body = Encoding.UTF8.GetBytes(text), result = new byte[Bytes]; int length = Math.Min(body.Length, Bytes - 17);
        if (length < body.Length) { while (length > 0 && (body[length] & 0xc0) == 0x80) { length--; } }
        BinaryPrimitives.WriteUInt32LittleEndian(result, 1); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)length); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), length < body.Length ? 1U : 0U);
        Array.Copy(body, 0, result, 16, length); return result;
    }
}
