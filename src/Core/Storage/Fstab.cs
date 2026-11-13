using System;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Storage;

/// <summary>What a usable fstab line mounts: the partition by GPT partition GUID (<c>PARTUUID=</c>)
/// or by device name (<c>/dev/sata0p2</c>), where, the filesystem (ext2, fat/vfat or auto) and
/// whether startup skips it (<c>noauto</c>).</summary>
internal sealed record FstabEntry(Guid? Partition, string? Device, string MountPoint, string Filesystem, bool NoAuto, bool System = false);

/// <summary>One line of the file exactly as written, and what Zenith made of it: an entry, nothing
/// (a comment or blank line), or the reason it is skipped.</summary>
internal sealed record FstabLine(string Text, FstabEntry? Entry, string? Problem);

/// <summary>
/// <c>/etc/fstab</c>: the partitions Zenith mounts at startup. People edit it by hand as well as
/// through Equinox › Mounts, so Zenith changes only the one line it is asked about and keeps every
/// other line byte for byte: comments, order, options, and lines it does not understand (those are
/// skipped at startup and reported, never removed). System mount points (<c>/</c>, <c>/boot</c>,
/// <c>/tmp</c>...) are mounted by the kernel itself; lines for them are kept and ignored.
/// Pure (host-tested); <see cref="SystemMounts"/> reads, edits and applies it.
/// </summary>
internal static class Fstab
{
    public const string Path = "/etc/fstab";
    public const int MaximumName = 15;
    public const string NewFileHeader = "# /etc/fstab: partitions Zenith mounts at startup. Equinox > Mounts edits the lines\n"
        + "# for the partitions it mounts and leaves every other line as written.\n"
        + "# <partition> <mount point> <filesystem> <options> <dump> <pass>\n";

    /// <summary>System mount points: never mounted, unmounted or replaced from Equinox or fstab.</summary>
    public static readonly string[] System = { "/", "/boot", "/boot/efi", "/tmp", "/proc", "/dev" };

    public static bool IsSystem(string mountPoint) => Array.IndexOf(System, mountPoint) >= 0;

    /// <summary>A mount name becomes <c>/mnt/name</c>: 1 to 15 letters, digits, '.', '_' or '-', not
    /// starting with a dot. Null when it is valid, else the reason.</summary>
    public static string? CheckName(string name)
    {
        if (name.Length is < 1 or > MaximumName) { return "a mount name has 1 to " + MaximumName + " characters"; }
        if (name[0] == '.') { return "a mount name cannot start with a dot"; }
        foreach (char c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            { return "a mount name uses letters, digits, '.', '_' and '-' only"; }
        }
        return null;
    }

    public static string PointFor(string name) => "/mnt/" + name;

    public static List<FstabLine> Parse(string? text)
    {
        var lines = new List<FstabLine>();
        if (string.IsNullOrEmpty(text)) { return lines; }
        string[] raw = text.Split('\n');
        int count = raw.Length;
        if (text.EndsWith('\n')) { count--; }   // the final newline ends the last line, it is not a line
        var points = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string line = raw[i].TrimEnd('\r');
            var parsed = ParseLine(line, out string? problem);
            if (parsed is not null && !points.Add(parsed.MountPoint)) { parsed = null; problem = "the mount point is listed twice; the first line wins"; }
            lines.Add(new(raw[i], parsed, parsed is null ? problem : null));
        }
        return lines;
    }

    private static FstabEntry? ParseLine(string line, out string? problem)
    {
        problem = null;
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#') { return null; }
        string[] f = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 3) { problem = "needs at least partition, mount point and filesystem"; return null; }
        // The root, /boot/efi, /tmp... (the installer writes these lines): the kernel mounts them
        // itself, so whatever names them is recorded, not checked.
        string where = f[1].Length > 1 ? f[1].TrimEnd('/') : f[1];
        if (IsSystem(where)) { return new(null, null, where, f[2], true, System: true); }
        Guid? id = null; string? device = null;
        if (f[0].StartsWith("PARTUUID=", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(f[0][9..], out Guid g) || g == Guid.Empty) { problem = "PARTUUID is not a GUID"; return null; }
            id = g;
        }
        else if (f[0].StartsWith("/dev/", StringComparison.Ordinal) && f[0].Length > 5 && f[0].IndexOf('/', 5) < 0) { device = f[0][5..]; }
        else { problem = "Zenith finds partitions by PARTUUID= or /dev/name only"; return null; }
        string point = f[1].Length > 1 ? f[1].TrimEnd('/') : f[1];
        if (!point.StartsWith('/') || point.Contains("/../") || point.EndsWith("/..") || point.Contains("/./")) { problem = "the mount point must be an absolute path"; return null; }
        string fs = f[2].ToLowerInvariant() switch { "ext2" => "ext2", "fat" or "vfat" or "fat32" => "fat", "auto" => "auto", _ => "" };
        if (fs.Length == 0) { problem = f[2] + ": Zenith mounts ext2 and FAT only"; return null; }
        bool noAuto = false;
        if (f.Length > 3) { foreach (string option in f[3].Split(',')) { if (option == "noauto") { noAuto = true; } } }
        return new(id, device, point, fs, noAuto);
    }

    public static string Serialize(IReadOnlyList<FstabLine> lines)
    {
        var b = new StringBuilder();
        foreach (var line in lines) { b.Append(line.Text).Append('\n'); }
        return b.ToString();
    }

    private static bool Names(FstabEntry entry, Guid partition, string? device)
        => !entry.System && (entry.Partition == partition || (device is not null && entry.Device == device));

    /// <summary>The line for this partition, if any (by PARTUUID, or its /dev name).</summary>
    public static FstabEntry? Find(IReadOnlyList<FstabLine> lines, Guid partition, string? device = null)
    {
        foreach (var line in lines) { if (line.Entry is FstabEntry e && Names(e, partition, device)) { return e; } }
        return null;
    }

    /// <summary>Mount this partition at <paramref name="mountPoint"/> at startup. An existing line for
    /// it is rewritten in place keeping its options and dump/pass fields; otherwise a line is added at
    /// the end. Refused when another line already uses the mount point.</summary>
    public static List<FstabLine> With(IReadOnlyList<FstabLine> lines, Guid partition, string? device, string mountPoint, string filesystem)
    {
        var result = new List<FstabLine>(); bool replaced = false;
        foreach (var line in lines)
        {
            if (line.Entry is FstabEntry e && Names(e, partition, device))
            {
                if (replaced) { continue; }   // a second line for the same partition: the first one wins
                string[] f = line.Text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string options = f.Length > 3 ? WithoutNoAuto(f[3]) : "defaults";
                string tail = f.Length > 4 ? " " + string.Join(' ', f, 4, f.Length - 4) : " 0 2";
                result.Add(Line("PARTUUID=" + partition.ToString("D") + " " + mountPoint + " " + filesystem + " " + options + tail));
                replaced = true; continue;
            }
            if (line.Entry is FstabEntry other && other.MountPoint == mountPoint)
            { throw new InvalidOperationException(mountPoint + " is already used at startup by another line of /etc/fstab"); }
            result.Add(line);
        }
        if (!replaced) { result.Add(Line("PARTUUID=" + partition.ToString("D") + " " + mountPoint + " " + filesystem + " defaults 0 2")); }
        return result;
    }

    /// <summary>Stop mounting this partition at startup: its lines go, every other line stays.</summary>
    public static List<FstabLine> Without(IReadOnlyList<FstabLine> lines, Guid partition, string? device = null)
    {
        var result = new List<FstabLine>();
        foreach (var line in lines) { if (!(line.Entry is FstabEntry e && Names(e, partition, device))) { result.Add(line); } }
        return result;
    }

    /// <summary>Lines startup skips (not understood), for Equinox to point at.</summary>
    public static int Skipped(IReadOnlyList<FstabLine> lines)
    {
        int n = 0;
        foreach (var line in lines) { if (line.Problem is not null) { n++; } }
        return n;
    }

    private static string WithoutNoAuto(string options)
    {
        var kept = new List<string>();
        foreach (string o in options.Split(',')) { if (o != "noauto" && o.Length > 0) { kept.Add(o); } }
        return kept.Count == 0 ? "defaults" : string.Join(',', kept);
    }

    private static FstabLine Line(string text) => new(text, ParseLine(text, out _), null);
}
