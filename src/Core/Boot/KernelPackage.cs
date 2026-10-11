using System;
using System.Globalization;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Zenith.Core.Packages;
using Zenith.Core.Security;

namespace Zenith.Core.Boot;

/// <summary>Dedicated signed kernel container; P1 never accepts its header or boot payload.</summary>
internal sealed record KernelPackage(string Version, string Id, byte[] Kernel)
{
    public const int MaximumBytes = BootLayout.MaximumKernelBytes + 4096;
    public const string Abi = "zenith-x64-boot-1";
    public static string Metadata(string version, byte[] kernel)
    {
        if (!PackageVersions.Valid(version)) { throw new IOException("invalid kernel version"); }
        ValidatePayload(kernel);
        return "ZENITH-KERNEL\t1\nname\tzenith-kernel\nversion\t" + version + "\narchitecture\tx86_64\nboot-abi\t" + Abi
            + "\nkernel-bytes\t" + kernel.Length + "\nkernel-sha256\t" + BootLayout.Digest(kernel) + "\n";
    }
    public static void ValidatePayload(ReadOnlySpan<byte> bytes)
    {
        BootLayout.ValidateKernel(bytes);
        if (bytes[6] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[52..]) != 64
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[54..]) != 56) { throw new IOException("invalid executable ELF header"); }
        ulong table = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), entry = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[56..]);
        if (count is < 1 or > 128 || table > (ulong)bytes.Length || (ulong)count * 56 > (ulong)bytes.Length - table) { throw new IOException("invalid ELF program table"); }
        bool executable = false;
        for (int i = 0; i < count; i++)
        {
            var header = bytes.Slice((int)table + i * 56, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 1) { continue; }
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]), address = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
            ulong file = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]), memory = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
            if (offset > (ulong)bytes.Length || file > (ulong)bytes.Length - offset || memory < file || address > ulong.MaxValue - memory)
            { throw new IOException("invalid ELF load segment bounds"); }
            if ((BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) & 1) != 0 && entry >= address && entry - address < file) { executable = true; }
        }
        if (!executable) { throw new IOException("ELF entry is not in file-backed executable code"); }
    }
    public static byte[] Content(string version, byte[] kernel)
    {
        byte[] text = Encoding.ASCII.GetBytes(Metadata(version, kernel));
        byte[] result = new byte[4 + text.Length + kernel.Length];
        for (int i = 0; i < 4; i++) { result[i] = (byte)(text.Length >> (8 * i)); }
        text.CopyTo(result, 4); kernel.CopyTo(result, 4 + text.Length); return result;
    }
    public static KernelPackage Read(byte[] bytes, byte[] publicKey, IPackageSignatureVerifier verifier)
    {
        if (bytes.Length > MaximumBytes || publicKey.Length is < 1 or > 4096) { throw new IOException("kernel package/key exceeds limit"); }
        int nl = Array.IndexOf(bytes, (byte)'\n', 0, Math.Min(256, bytes.Length));
        if (nl < 0) { throw new IOException("invalid kernel package header"); }
        string[] h = Encoding.ASCII.GetString(bytes, 0, nl).Split('\t');
        if (h.Length != 5 || h[0] != "ZENITH-SIGNED-KERNEL" || h[1] != "1" || h[2] != BootLayout.Digest(publicKey)
            || !int.TryParse(h[3], NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size < 68 || size > MaximumBytes - 1024
            || size.ToString(CultureInfo.InvariantCulture) != h[3] || !int.TryParse(h[4], out int signature) || signature is not (256 or 384 or 512)
            || signature.ToString(CultureInfo.InvariantCulture) != h[4] || bytes.Length != nl + 1 + size + signature)
        { throw new IOException("invalid kernel package length/key identity"); }
        byte[] hash = new byte[32]; Sha256.Hash(bytes.AsSpan(0, nl + 1 + size), hash);
        if (!verifier.Verify(publicKey, hash, bytes.AsSpan(nl + 1 + size, signature).ToArray())) { throw new IOException("kernel signature rejected"); }
        int start = nl + 1; int length = bytes[start] | bytes[start + 1] << 8 | bytes[start + 2] << 16 | bytes[start + 3] << 24;
        if (length is < 1 or > 1024 || length > size - 68) { throw new IOException("invalid kernel metadata length"); }
        string metadata = new UTF8Encoding(false, true).GetString(bytes, start + 4, length);
        string[] lines = metadata.Split('\n');
        if (lines.Length != 8 || lines[2].Split('\t').Length != 2) { throw new IOException("invalid kernel metadata"); }
        string version = lines[2].Split('\t')[1]; byte[] kernel = bytes.AsSpan(start + 4 + length, size - 4 - length).ToArray();
        if (metadata != Metadata(version, kernel)) { throw new IOException("kernel metadata, platform or payload mismatch"); }
        return new(version, BootLayout.Digest(kernel), kernel);
    }
}
