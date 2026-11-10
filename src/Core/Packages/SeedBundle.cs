// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zenith.Core.Packages;

internal enum SeedKind { PackageKey, TlsCa, Package }

internal sealed record SeedFile(SeedKind Kind, string Name, int Offset, int Length, string Sha256);

/// <summary>
/// The installer seed an ISO carries as an optional boot module (<c>/boot/zenith-seed.zsd</c>): the
/// package signing key, the TLS CA and the mirror the installed system should trust, and the signed
/// packages Sunrise installs with it. The seed itself is as trusted as the ISO that carries it;
/// packages are still verified against the seeded key. Pure, host-tested.
/// <code>
/// ZENITH-SEED\t1
/// mirror\tHTTPS_URL            (optional, at most once)
/// file\tKIND\tNAME\tSIZE\tSHA256 (KIND: package-key, tls-ca, package)
/// end
/// payloads, in the order listed
/// </code>
/// </summary>
internal sealed class SeedBundle
{
    public const int MaximumHeader = 16384, MaximumFiles = 32, MaximumPemBytes = 4096;
    public const string ModuleName = "zenith-seed";
    public string? Mirror { get; }
    public IReadOnlyList<SeedFile> Files { get; }
    private SeedBundle(string? mirror, IReadOnlyList<SeedFile> files) { Mirror = mirror; Files = files; }

    public SeedFile? Single(SeedKind kind)
    {
        foreach (var file in Files) { if (file.Kind == kind) { return file; } }
        return null;
    }

    public IEnumerable<SeedFile> Packages()
    {
        foreach (var file in Files) { if (file.Kind == SeedKind.Package) { yield return file; } }
    }

    /// <summary>Parses and checks every payload's SHA256; throws <see cref="IOException"/> on any defect.</summary>
    public static SeedBundle Parse(ReadOnlySpan<byte> bytes)
    {
        int end = -1;
        for (int i = 0; i < Math.Min(bytes.Length, MaximumHeader) - 4; i++)
        {
            if (bytes[i] == '\n' && bytes[i + 1] == 'e' && bytes[i + 2] == 'n' && bytes[i + 3] == 'd' && bytes[i + 4] == '\n') { end = i + 5; break; }
        }
        if (end < 0) { throw new IOException("seed header missing or too large"); }
        string header;
        try { header = new UTF8Encoding(false, true).GetString(bytes[..end]); }
        catch (DecoderFallbackException) { throw new IOException("seed header is not UTF-8"); }
        string[] lines = header.Split('\n');
        if (lines[0] != "ZENITH-SEED\t1") { throw new IOException("not a version 1 Zenith seed"); }
        string? mirror = null; var files = new List<SeedFile>(); var names = new HashSet<string>(StringComparer.Ordinal);
        long offset = end;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == "end") { break; }
            string[] fields = line.Split('\t');
            if (fields[0] == "mirror" && fields.Length == 2)
            {
                if (mirror is not null) { throw new IOException("seed lists more than one mirror"); }
                if (!ValidMirror(fields[1])) { throw new IOException("seed mirror must be an https base URL"); }
                mirror = fields[1].TrimEnd('/');
                continue;
            }
            if (fields[0] != "file" || fields.Length != 5) { throw new IOException("unknown seed line: " + line); }
            SeedKind kind = fields[1] switch
            {
                "package-key" => SeedKind.PackageKey,
                "tls-ca" => SeedKind.TlsCa,
                "package" => SeedKind.Package,
                _ => throw new IOException("unknown seed file kind: " + fields[1]),
            };
            string name = fields[2];
            if (name.Length is 0 or > 64 || !names.Add(name)) { throw new IOException("invalid or duplicate seed file name"); }
            foreach (char c in name) { if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_')) { throw new IOException("invalid seed file name: " + name); } }
            if (!int.TryParse(fields[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int length)
                || length <= 0 || length > SignedPackage.MaximumBytes || (kind != SeedKind.Package && length > MaximumPemBytes))
            { throw new IOException("invalid seed file size: " + name); }
            string sha = fields[4];
            if (sha.Length != 64) { throw new IOException("invalid seed file SHA256: " + name); }
            if (kind != SeedKind.Package && files.Exists(f => f.Kind == kind)) { throw new IOException("seed lists more than one " + fields[1]); }
            if (files.Count == MaximumFiles) { throw new IOException("seed lists too many files"); }
            if (offset + length > bytes.Length) { throw new IOException("seed is truncated: " + name); }
            if (!PackagePlanner.Digest(bytes.Slice((int)offset, length)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            { throw new IOException("seed file SHA256 mismatch: " + name); }
            files.Add(new SeedFile(kind, name, (int)offset, length, sha.ToLowerInvariant()));
            offset += length;
        }
        if (offset != bytes.Length) { throw new IOException("seed has trailing bytes"); }
        if (files.Exists(f => f.Kind == SeedKind.Package) && !files.Exists(f => f.Kind == SeedKind.PackageKey))
        { throw new IOException("seed packages need the seed's package key"); }
        return new SeedBundle(mirror, files);
    }

    private static bool ValidMirror(string url)
    {
        // The same rule as pkg mirror: repositories are fetched over verified HTTPS only.
        if (url.Length > 200 || !url.StartsWith("https://", StringComparison.Ordinal) || url.Length == 8) { return false; }
        foreach (char c in url) { if (c <= ' ' || c > '~') { return false; } }
        return true;
    }

    /// <summary>Writes a seed: the host tool's format, kept here so tests round-trip it.</summary>
    public static byte[] Build(string? mirror, IReadOnlyList<(SeedKind Kind, string Name, byte[] Data)> files)
    {
        var header = new StringBuilder("ZENITH-SEED\t1\n");
        if (mirror is not null) { header.Append("mirror\t").Append(mirror).Append('\n'); }
        foreach (var (kind, name, data) in files)
        {
            string label = kind switch { SeedKind.PackageKey => "package-key", SeedKind.TlsCa => "tls-ca", _ => "package" };
            header.Append("file\t").Append(label).Append('\t').Append(name).Append('\t').Append(data.Length).Append('\t')
                .Append(PackagePlanner.Digest(data)).Append('\n');
        }
        header.Append("end\n");
        var output = new MemoryStream();
        byte[] text = Encoding.UTF8.GetBytes(header.ToString()); output.Write(text, 0, text.Length);
        foreach (var file in files) { output.Write(file.Data, 0, file.Data.Length); }
        return output.ToArray();
    }
}
