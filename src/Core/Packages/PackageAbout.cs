using System;
using System.Collections.Generic;
using System.Text;

namespace Zenith.Core.Packages;

/// <summary>A package's own words about itself (Orbit, kernel 0.0.70): the optional signed file
/// <c>/usr/share/zenith/about/NAME.about</c> inside the package. Older kernels see an ordinary data file.
/// <code>
/// ZENITH-ABOUT	1
/// title	Polaris
/// summary	The native terminal
/// description	...           (up to 8 lines, joined)
/// category	apps|system|tools|fonts|themes|documents
/// icon	terminal
/// replaces	Opens instead of the built-in Terminal, which stays for recovery
/// change	0.1.0	Scrollback, selection, clipboard      (newest first, up to 16)
/// planned	Tabs and layouts                              (up to 4)
/// </code></summary>
internal sealed class PackageAbout
{
    public const string Directory = "/usr/share/zenith/about/";
    public const int MaximumBytes = 8192;
    public string Title { get; private set; } = "";
    public string Summary { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string Icon { get; private set; } = "";
    public string Replaces { get; private set; } = "";
    public List<(string Version, string Text)> Changes { get; } = new();
    public List<string> Planned { get; } = new();

    public static string PathOf(string name) => Directory + name + ".about";

    /// <summary>Lenient: unknown or malformed lines are skipped, so a newer about file still reads.</summary>
    public static PackageAbout Parse(string text)
    {
        var about = new PackageAbout();
        if (text.Length > MaximumBytes) { text = text.Substring(0, MaximumBytes); }
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "ZENITH-ABOUT\t1") { return about; }
        var description = new StringBuilder(); int descriptionLines = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].TrimEnd('\r').Split('\t');
            if (f.Length < 2) { continue; }
            string value = PackageHistory.Clean(f[1], 240);
            switch (f[0])
            {
                case "title": about.Title = PackageHistory.Clean(f[1], 40); break;
                case "summary": about.Summary = PackageHistory.Clean(f[1], 96); break;
                case "description":
                    if (descriptionLines++ < 8) { if (description.Length > 0) { description.Append(' '); } description.Append(value); }
                    break;
                case "category": about.Category = PackageHistory.Clean(f[1], 16).ToLowerInvariant(); break;
                case "icon": about.Icon = PackageHistory.Clean(f[1], 24); break;
                case "replaces": about.Replaces = value; break;
                case "change" when f.Length >= 3 && PackageVersions.Valid(f[1]) && about.Changes.Count < 16:
                    about.Changes.Add((f[1], PackageHistory.Clean(f[2], 160))); break;
                case "planned" when about.Planned.Count < 4: about.Planned.Add(PackageHistory.Clean(f[1], 160)); break;
            }
        }
        about.Description = description.Length > 1000 ? description.ToString(0, 1000) : description.ToString();
        return about;
    }

    /// <summary>What a package can do, read from what it declares: the kernel interfaces it requires and whether it
    /// carries programs. Each line: (title, plain-words detail, allowed). Shown as sentences, including what it
    /// cannot do.</summary>
    public static List<(string Title, string Detail, bool Allowed)> Capabilities(PackageManifest manifest)
    {
        var result = new List<(string, string, bool)>();
        var needs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies) { needs.Add(dependency.Name); }
        bool programs = false;
        foreach (var file in manifest.Files) { if (file.Path.EndsWith(".elf", StringComparison.Ordinal) && file.Mode == 0x1ed) { programs = true; } }
        if (manifest.Kind == "meta")
        {
            result.Add(("Installs a set of packages", "Brings in what it lists; it has no files of its own", true));
            return result;
        }
        if (!programs)
        {
            result.Add(("Runs nothing", "Only files: fonts, icons, documents, themes or settings", true));
            return result;
        }
        if (needs.Contains("zenith-native-terminal-session-abi")) { result.Add(("Run a shell as you", "Same rights you have, nothing more", true)); }
        result.Add(needs.Contains("zenith-native-graphics-abi") ? ("Open windows", "Its own windows only", true) : ("Open windows", "No, it runs in a terminal", false));
        result.Add(needs.Contains("zenith-native-network-abi") ? ("Use the network", "Connect to servers, with verified TLS for secure sites", true) : ("Use the network", "No", false));
        if (needs.Contains("zenith-native-network-center-abi")) { result.Add(("Change the network connection", "Only after you allow it", true)); }
        if (needs.Contains("zenith-native-files-abi")) { result.Add(("Manage your files", "Copy, move and delete with your rights; other users' files only after you allow it", true)); }
        else { result.Add(("Read your files", "Only files you can read, when you open them", true)); }
        if (needs.Contains("zenith-native-disk-abi")) { result.Add(("Manage disks", "Partition and format only after you allow it", true)); }
        if (needs.Contains("zenith-native-package-center-abi")) { result.Add(("Install and remove packages", "Only after you allow it", true)); }
        if (needs.Contains("zenith-native-system-settings-abi") || needs.Contains("zenith-native-display-abi"))
        { result.Add(("Change settings for everyone", "Only after you allow it", true)); }
        if (needs.Contains("zenith-native-audio-abi")) { result.Add(("Play sound", "Recording only after you allow it", true)); }
        if (needs.Contains("zenith-native-monitor-abi")) { result.Add(("See running programs", "Stop only your own, or others after you allow it", true)); }
        if (needs.Contains("zenith-native-clipboard-abi")) { result.Add(("Use the clipboard", "When you copy and paste", true)); }
        result.Add(("Run as root", "Never by itself", false));
        return result;
    }

    /// <summary>A kernel interface's requirement in plain words, for "What installing changes".</summary>
    public static string Need(PackageDependency dependency)
    {
        if (dependency.Name == "zenith-base") { return "Zenith " + dependency.Version; }
        foreach (var component in BootstrapComponents.All)
        { if (component.Name == dependency.Name && component.Name.EndsWith("-abi", StringComparison.Ordinal)) { return "kernel interface " + dependency.Name + " " + dependency.Version; } }
        return dependency.Name + " " + dependency.Version;
    }
}
