using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Zenith.Core.Packages;

namespace Zenith.Core.Boot;

internal sealed record KernelGeneration(string Id, int Bytes, string Version)
{
    public string Serialize() => Id + "\t" + Bytes.ToString(CultureInfo.InvariantCulture) + "\t" + Version;
    public static KernelGeneration Parse(string text)
    {
        string[] f = text.Split('\t');
        if (f.Length != 3 || !BootLayout.ValidId(f[0]) || !int.TryParse(f[1], out int size) || size is < 64 or > BootLayout.MaximumKernelBytes
            || size.ToString(CultureInfo.InvariantCulture) != f[1] || !PackageVersions.Valid(f[2])) { throw new IOException("invalid kernel generation record"); }
        return new(f[0], size, f[2]);
    }
}

internal sealed record KernelUpdateState(Guid Root, Guid Esp, string EfiId, int EfiBytes,
    KernelGeneration Current, KernelGeneration? Previous, KernelGeneration? Retired, bool Pending, string HighWater)
{
    public string Serialize() => "ZENITH-GENERATIONS\t1\nroot\t" + Root.ToString("D") + "\nesp\t" + Esp.ToString("D")
        + "\nefi\t" + EfiId + "\nefi-bytes\t" + EfiBytes.ToString(CultureInfo.InvariantCulture)
        + "\ncurrent\t" + Current.Serialize() + "\nprevious\t" + (Previous?.Serialize() ?? "-")
        + "\nretired\t" + (Retired?.Serialize() ?? "-") + "\npending\t" + (Pending ? "yes" : "no") + "\nhigh-water\t" + HighWater + "\n";
    public string Inventory() => BootLayout.Inventory(Root, Esp, Current.Id, EfiId, Current.Bytes, EfiBytes)
        .Replace("previous\t-\n", "previous\t" + (Previous?.Id ?? "-") + "\n");
    public string Menu() => BootLayout.Configuration(Root, Current.Id, Previous?.Id);
    public static KernelUpdateState Parse(string text)
    {
        var f = Fields(text, "ZENITH-GENERATIONS\t1", 9);
        if (!Guid.TryParseExact(f["root"], "D", out var root) || !Guid.TryParseExact(f["esp"], "D", out var esp)
            || root == Guid.Empty || esp == Guid.Empty || !BootLayout.ValidId(f["efi"]) || !int.TryParse(f["efi-bytes"], out int bytes)
            || bytes is < 256 or > BootLayout.MaximumEfiBytes || f["pending"] is not ("yes" or "no") || !PackageVersions.Valid(f["high-water"]))
        { throw new IOException("invalid boot generation identity"); }
        var result = new KernelUpdateState(root, esp, f["efi"], bytes, KernelGeneration.Parse(f["current"]),
            f["previous"] == "-" ? null : KernelGeneration.Parse(f["previous"]), f["retired"] == "-" ? null : KernelGeneration.Parse(f["retired"]),
            f["pending"] == "yes", f["high-water"]);
        if (result.Serialize() != text || result.Current.Id == result.Previous?.Id || result.Current.Id == result.Retired?.Id
            || (result.Retired is not null && result.Retired.Id == result.Previous?.Id) || (result.Pending && result.Previous is null)
            || PackageVersions.Compare(result.HighWater, result.Current.Version) < 0) { throw new IOException("inconsistent boot generations"); }
        return result;
    }
    public static KernelUpdateState Baseline(string text)
    {
        var f = Fields(text, "ZENITH-BOOT\t1", 8);
        if (f["architecture"] != "x86_64" || f["previous"] != "-") { throw new IOException("unsupported legacy boot inventory"); }
        var result = new KernelUpdateState(Guid.Parse(f["root"]), Guid.Parse(f["esp"]), f["efi-sha256"], int.Parse(f["efi-bytes"]),
            new(f["current"], int.Parse(f["kernel-bytes"]), "0.0.0"), null, null, false, "0.0.0");
        _ = Parse(result.Serialize()); return result;
    }
    private static Dictionary<string, string> Fields(string text, string header, int count)
    {
        if (text.Length > 4096) { throw new IOException("boot metadata exceeds bound"); }
        string[] lines = text.Split('\n');
        if (lines.Length != count + 2 || lines[0] != header || lines[^1] != "") { throw new IOException("invalid boot metadata"); }
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i <= count; i++) { int tab = lines[i].IndexOf('\t'); if (tab < 1 || !f.TryAdd(lines[i][..tab], lines[i][(tab + 1)..])) { throw new IOException("duplicate/invalid boot metadata"); } }
        return f;
    }
}
