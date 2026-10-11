using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Zenith.Core.Security;

namespace Zenith.Core.Packages;

internal interface IPackageSignatureVerifier
{
    bool Verify(byte[] publicKey, byte[] digest, byte[] signature);
}

/// <summary>Offline v1 container: signed header/manifest/payload, bounded uncompressed files.</summary>
internal sealed class SignedPackage : IPackageSource
{
    public const int MaximumBytes = PackageManifest.MaximumPayloadBytes + PackageManifest.MaximumBytes + 2048;
    public PackageManifest Manifest { get; }
    public string ArchiveDigest { get; private set; } = "";
    public string KeyId { get; private set; } = "";
    public string ManifestText { get; }
    private readonly Dictionary<string, byte[]> _files;
    private SignedPackage(PackageManifest manifest, string text, Dictionary<string, byte[]> files)
    { Manifest = manifest; ManifestText = text; _files = files; }
    public byte[] Read(PackageFile file) => _files[file.Path];
    public static SignedPackage Read(byte[] bytes, byte[] publicKey, IPackageSignatureVerifier verifier)
    {
        if (bytes.Length > MaximumBytes || publicKey.Length > 4096 || publicKey.Length == 0) { throw new IOException("package/key exceeds limit"); }
        int newline = Array.IndexOf(bytes, (byte)'\n', 0, Math.Min(256, bytes.Length));
        if (newline < 0) { throw new IOException("invalid signed package header"); }
        string[] header = Encoding.ASCII.GetString(bytes, 0, newline).Split('\t');
        if (header.Length != 5 || header[0] != "ZENITH-SIGNED-PACKAGE" || header[1] != "1"
            || header[2] != PackagePlanner.Digest(publicKey)
            || !int.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out int content)
            || content < 1 || content > MaximumBytes - 1024 || content.ToString(CultureInfo.InvariantCulture) != header[3]
            || !int.TryParse(header[4], out int signature) || signature is not (256 or 384 or 512)
            || signature.ToString(CultureInfo.InvariantCulture) != header[4]
            || bytes.Length != newline + 1 + content + signature) { throw new IOException("invalid package length/key identity"); }
        byte[] hash = new byte[32]; Sha256.Hash(bytes.AsSpan(0, newline + 1 + content), hash);
        if (!verifier.Verify(publicKey, hash, bytes.AsSpan(newline + 1 + content, signature).ToArray()))
        { throw new IOException("package signature rejected"); }
        int offset = newline + 1, end = offset + content;
        if (end - offset < 4) { throw new IOException("truncated manifest length"); }
        int length = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24; offset += 4;
        if (length < 1 || length > PackageManifest.MaximumBytes || length > end - offset) { throw new IOException("invalid manifest length"); }
        string text = new UTF8Encoding(false, true).GetString(bytes, offset, length); offset += length;
        var manifest = PackageManifest.Parse(text); var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file.Length > end - offset) { throw new IOException("truncated payload"); }
            byte[] data = bytes.AsSpan(offset, file.Length).ToArray(); offset += file.Length;
            if (PackagePlanner.Digest(data) != file.Digest) { throw new IOException("payload digest mismatch"); }
            files.Add(file.Path, data);
        }
        if (offset != end) { throw new IOException("trailing package data"); }
        return new SignedPackage(manifest, text, files) { ArchiveDigest = PackagePlanner.Digest(bytes), KeyId = header[2] };
    }
    public static byte[] Content(string manifestText, IPackageSource source)
    {
        var manifest = PackageManifest.Parse(manifestText); byte[] text = Encoding.UTF8.GetBytes(manifestText);
        using var output = new MemoryStream(); int size = text.Length;
        output.WriteByte((byte)size); output.WriteByte((byte)(size >> 8)); output.WriteByte((byte)(size >> 16)); output.WriteByte((byte)(size >> 24)); output.Write(text);
        foreach (var file in manifest.Files)
        {
            byte[] bytes = source.Read(file);
            if (bytes.Length != file.Length || PackagePlanner.Digest(bytes) != file.Digest) { throw new IOException("payload integrity mismatch"); }
            output.Write(bytes);
        }
        return output.ToArray();
    }
}
