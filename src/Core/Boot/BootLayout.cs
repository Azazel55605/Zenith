using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zenith.Core.Security;

namespace Zenith.Core.Boot;

/// <summary>Versioned x64 UEFI artifacts; future package activation publishes the menu last.</summary>
internal static class BootLayout
{
    public const ulong EspBytes = 128UL * 1024 * 1024;
    public const ulong MinimumDiskBytes = 256UL * 1024 * 1024;
    public const int MaximumKernelBytes = 32 * 1024 * 1024;
    public const int MaximumEfiBytes = 2 * 1024 * 1024;
    public static string Digest(ReadOnlySpan<byte> bytes)
    {
        byte[] hash = new byte[32]; Sha256.Hash(bytes, hash);
        var text = new StringBuilder(64);
        foreach (byte b in hash) { text.Append(b.ToString("x2")); }
        return text.ToString();
    }
    public static bool ValidId(string id)
    {
        if (id.Length != 64) { return false; }
        foreach (char c in id) { if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return false; } }
        return true;
    }
    public static string KernelPath(string id)
    {
        if (!ValidId(id)) { throw new IOException("invalid kernel generation"); }
        return "/boot/zenith/" + id + "/kernel.elf";
    }
    public static void ValidateKernel(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64 || bytes.Length > MaximumKernelBytes || bytes[0] != 0x7f
            || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' || bytes[4] != 2
            || bytes[5] != 1 || bytes[18] != 62 || bytes[19] != 0)
        { throw new IOException("expected bounded x64 ELF installation kernel"); }
    }
    public static void ValidateEfi(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 256 || bytes.Length > MaximumEfiBytes || bytes[0] != 'M' || bytes[1] != 'Z')
        { throw new IOException("expected bounded x64 UEFI installer module"); }
        int offset = bytes[60] | bytes[61] << 8 | bytes[62] << 16 | bytes[63] << 24;
        if (offset < 64 || offset > bytes.Length - 24 || bytes[offset] != 'P' || bytes[offset + 1] != 'E'
            || bytes[offset + 2] != 0 || bytes[offset + 3] != 0 || bytes[offset + 4] != 0x64 || bytes[offset + 5] != 0x86)
        { throw new IOException("invalid x64 PE/COFF installer module"); }
    }
    /// <summary>Boot opt-ins a rewritten menu keeps when the running kernel was booted with them.</summary>
    public static readonly string[] CarriedOptions = { "smp=native", "rtc=utc", "rtc=local" };
    public static string Carried(string arguments)
    {
        var tokens = arguments.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (string option in CarriedOptions) { if (Array.IndexOf(tokens, option) >= 0) { kept.Add(option); } }
        return string.Join(" ", kept);
    }
    public static string Configuration(Guid root, string current, string? previous = null, string options = "")
    {
        if (root == Guid.Empty) { throw new IOException("root partition UUID missing"); }
        foreach (string option in options.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        { if (Array.IndexOf(CarriedOptions, option) < 0) { throw new IOException("boot option is not carried: " + option); } }
        options = string.Join(" ", options.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        _ = KernelPath(current);
        if (previous is not null) { _ = KernelPath(previous); if (previous == current) { throw new IOException("duplicate kernel generation"); } }
        var result = new StringBuilder("timeout: 5\n\n");
        Entry(result, "Zenith", root, current, options);
        if (previous is not null) { Entry(result, "Zenith previous kernel", root, previous, options); }
        result.Append("/Zenith rescue (RAM root)\n    protocol: limine\n    path: boot():")
            .Append(KernelPath(previous ?? current)).Append("\n    cmdline: root=live session=headless\n");
        return WithDisplay(result.ToString(), 1920, 1080);
    }
    private static void Entry(StringBuilder text, string title, Guid root, string id, string options)
    {
        text.Append('/').Append(title).Append("\n    protocol: limine\n    path: boot():")
            .Append(KernelPath(id)).Append("\n    cmdline: root=PARTUUID=").Append(root.ToString("D"))
            .Append(" kernel=").Append(id).Append(options.Length == 0 ? "" : " " + options).Append('\n');
    }
    public static string WithDisplay(string menu, int width, int height)
    {
        if (width is < 640 or > 16384 || height is < 480 or > 16384 || menu.Length > 32768)
        { throw new IOException("invalid boot display preference"); }
        string mode = width + "x" + height;
        var text = new StringBuilder("interface_resolution: " + mode + "\n");
        foreach (string line in menu.Split('\n'))
        {
            string option = line.TrimStart();
            if (option.StartsWith("interface_resolution:", StringComparison.OrdinalIgnoreCase)
                || option.StartsWith("resolution:", StringComparison.OrdinalIgnoreCase)) { continue; }
            text.Append(line).Append('\n');
            if (option.Equals("protocol: limine", StringComparison.OrdinalIgnoreCase))
            { text.Append("    resolution: ").Append(mode).Append("x32\n"); }
        }
        return text.ToString().TrimEnd('\n') + "\n";
    }
    public static string PreserveDisplay(string oldMenu, string newMenu)
    {
        foreach (string line in oldMenu.Split('\n'))
        {
            string option = line.Trim();
            const string prefix = "interface_resolution:";
            if (!option.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { continue; }
            string[] size = option[prefix.Length..].Trim().Split('x');
            if (size.Length == 2 && int.TryParse(size[0], out int w) && int.TryParse(size[1], out int h)
                && w is >= 640 and <= 16384 && h is >= 480 and <= 16384) { return WithDisplay(newMenu, w, h); }
        }
        return newMenu;
    }
    public static string Inventory(Guid root, Guid esp, string kernel, string efi, int kernelBytes, int efiBytes)
    {
        if (root == Guid.Empty || esp == Guid.Empty || !ValidId(kernel) || !ValidId(efi)) { throw new IOException("invalid installed boot identity"); }
        return "ZENITH-BOOT\t1\narchitecture\tx86_64\nroot\t" + root.ToString("D") + "\nesp\t" + esp.ToString("D")
            + "\ncurrent\t" + kernel + "\nprevious\t-\nkernel-bytes\t" + kernelBytes + "\nefi-sha256\t" + efi + "\nefi-bytes\t" + efiBytes + "\n";
    }
}

internal sealed record BootRootSelection(bool Live, Guid? PartitionId, string? KernelId)
{
    public static BootRootSelection Parse(string arguments)
    {
        if (arguments.Length > 4096) { throw new IOException("boot arguments exceed limit"); }
        bool live = false, rootSeen = false; Guid? id = null; string? kernel = null;
        foreach (string token in arguments.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("root=", StringComparison.Ordinal))
            {
                if (rootSeen) { throw new IOException("duplicate root selector"); } rootSeen = true;
                string value = token.Substring(5);
                if (value == "live") { live = true; }
                else if (value != "auto")
                {
                    if (!value.StartsWith("PARTUUID=", StringComparison.Ordinal) || !Guid.TryParseExact(value.Substring(9), "D", out var parsed) || parsed == Guid.Empty)
                    { throw new IOException("invalid root selector (use live, auto or PARTUUID=UUID)"); }
                    id = parsed;
                }
            }
            else if (token.StartsWith("kernel=", StringComparison.Ordinal))
            {
                if (kernel is not null || !BootLayout.ValidId(token.Substring(7))) { throw new IOException("invalid/duplicate kernel generation"); }
                kernel = token.Substring(7);
            }
        }
        return new(live, id, kernel);
    }
}
