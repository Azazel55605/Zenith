using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Zenith.Core.Boot;
using Zenith.Core.Security;

namespace Zenith.Core.Packages;

internal sealed record RepositoryEntry(string Repository, string Kind, string Name, string Version,
    int Bytes, string Digest, string ManifestText)
{
    public string ArchivePath => Repository + "/" + Name + "/" + Version + "/" + Name + ".zpk";
    public PackageManifest Manifest => PackageManifest.Parse(ManifestText);
}

/// <summary>Private-repository protocol; authenticated, bounded snapshots, not TUF.</summary>
internal sealed record RepositoryIndex(string Repository, ulong Generation, long Issued, long Expires,
    IReadOnlyList<RepositoryEntry> Entries)
{
    public const int MaximumBytes = 262144;
    public void Fresh(long now)
    {
        if (Issued > now + 300 || Expires <= now) { throw new IOException("repository metadata expired or clock is incorrect; run pkg refresh"); }
    }
    public static string Content(string repository, ulong generation, long issued, long expires, IReadOnlyList<RepositoryEntry> entries)
    {
        var text = new StringBuilder("ZENITH-INDEX\t1\t" + repository + "\t" + generation.ToString(CultureInfo.InvariantCulture)
            + "\t" + issued.ToString(CultureInfo.InvariantCulture) + "\t" + expires.ToString(CultureInfo.InvariantCulture) + "\n");
        foreach (var entry in entries)
        {
            if (entry.Repository != repository) { throw new IOException("index repository mismatch"); }
            text.Append(entry.Kind).Append('\t').Append(entry.Name).Append('\t').Append(entry.Version).Append('\t')
                .Append(entry.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(entry.Digest).Append('\t')
                .Append(entry.Kind == "kernel" ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.ManifestText))).Append('\n');
        }
        string result = text.ToString(); _ = Parse(result); return result;
    }
    private static RepositoryIndex Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes - 1024 || !text.EndsWith('\n')) { throw new IOException("index content exceeds bound or is truncated"); }
        string[] lines = text.Split('\n'), header = lines[0].Split('\t');
        if (header.Length != 6 || header[0] != "ZENITH-INDEX" || header[1] != "1" || header[2] is not ("core" or "extra")
            || !ulong.TryParse(header[3], NumberStyles.None, CultureInfo.InvariantCulture, out ulong generation) || generation == 0
            || generation.ToString(CultureInfo.InvariantCulture) != header[3]
            || !long.TryParse(header[4], NumberStyles.None, CultureInfo.InvariantCulture, out long issued)
            || !long.TryParse(header[5], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
            || issued < 0 || expires <= issued || expires - issued > 30 * 86400L
            || issued.ToString(CultureInfo.InvariantCulture) != header[4] || expires.ToString(CultureInfo.InvariantCulture) != header[5])
        { throw new IOException("invalid index header/generation/validity"); }
        var entries = new List<RepositoryEntry>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length - 1; i++)
        {
            string[] f = lines[i].Split('\t');
            if (f.Length != 6 || f[0] is not ("data" or "kernel") || !PackageManifest.ValidName(f[1])
                || !PackageVersions.Valid(f[2]) || !identities.Add(f[1] + "@" + f[2]) || entries.Count == 64
                || !int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int bytes) || bytes < 1
                || bytes > (f[0] == "kernel" ? KernelPackage.MaximumBytes : SignedPackage.MaximumBytes)
                || bytes.ToString(CultureInfo.InvariantCulture) != f[3] || !Hex(f[4]))
            { throw new IOException("invalid/duplicate index entry"); }
            string manifest = "";
            if (f[0] == "kernel")
            {
                if (header[2] != "core" || f[1] != "zenith-kernel" || f[5] != "-") { throw new IOException("invalid indexed kernel identity"); }
            }
            else
            {
                if (f[5].Length > PackageManifest.MaximumBytes * 2) { throw new IOException("indexed manifest exceeds bound"); }
                manifest = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(f[5]));
                var parsed = PackageManifest.Parse(manifest);
                if (parsed.Name != f[1] || parsed.Version != f[2] || parsed.Repository != header[2]) { throw new IOException("indexed manifest identity mismatch"); }
            }
            entries.Add(new(header[2], f[0], f[1], f[2], bytes, f[4], manifest));
        }
        return new(header[2], generation, issued, expires, entries.AsReadOnly());
    }
    private static bool Hex(string text)
    {
        if (text.Length != 64) { return false; }
        foreach (char c in text) { if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return false; } }
        return true;
    }
    public static RepositoryIndex Read(byte[] bytes, byte[] key, IPackageSignatureVerifier verifier, string repository)
    {
        if (bytes.Length > MaximumBytes || key.Length is < 1 or > 4096) { throw new IOException("index/key exceeds bound"); }
        int newline = Array.IndexOf(bytes, (byte)'\n', 0, Math.Min(256, bytes.Length));
        if (newline < 0) { throw new IOException("invalid signed index header"); }
        string[] h = Encoding.ASCII.GetString(bytes, 0, newline).Split('\t');
        if (h.Length != 5 || h[0] != "ZENITH-SIGNED-INDEX" || h[1] != "1" || h[2] != PackagePlanner.Digest(key)
            || !int.TryParse(h[3], NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size < 1 || size > MaximumBytes - 1024
            || size.ToString(CultureInfo.InvariantCulture) != h[3] || !int.TryParse(h[4], out int sig) || sig is not (256 or 384 or 512)
            || sig.ToString(CultureInfo.InvariantCulture) != h[4] || bytes.Length != newline + 1 + size + sig)
        { throw new IOException("invalid index size/key identity"); }
        byte[] digest = new byte[32]; Sha256.Hash(bytes.AsSpan(0, newline + 1 + size), digest);
        if (!verifier.Verify(key, digest, bytes.AsSpan(newline + 1 + size, sig).ToArray())) { throw new IOException("repository signature rejected"); }
        var index = Parse(new UTF8Encoding(false, true).GetString(bytes, newline + 1, size));
        if (index.Repository != repository) { throw new IOException("wrong signed repository"); }
        return index;
    }
    public static void Advance(RepositoryIndex previous, byte[] oldBytes, RepositoryIndex next, byte[] newBytes)
    {
        if (previous.Repository != next.Repository || next.Generation < previous.Generation
            || (next.Generation == previous.Generation && PackagePlanner.Digest(oldBytes) != PackagePlanner.Digest(newBytes)))
        { throw new IOException("repository rollback or changed generation refused"); }
    }
}
