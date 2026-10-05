using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Keyboard.ScanMaps;

namespace Zenith.Core.Input;

/// <summary>
/// Keyboard layouts by their Linux keymap names. The saved choice lives in
/// <c>/etc/vconsole.conf</c> (<c>KEYMAP=de</c>), as on systemd systems, and is applied at boot.
/// </summary>
internal static class KeyboardLayouts
{
    public const string ConfigPath = "/etc/vconsole.conf";

    private static readonly Dictionary<string, (string Description, Func<ScanMapBase> Create)> s_layouts = new()
    {
        ["us"] = ("English (US), QWERTY", () => new USStandardLayout()),
        ["de"] = ("German, QWERTZ", () => new DEStandardLayout()),
        ["fr"] = ("French, AZERTY", () => new FRStandardLayout()),
        ["es"] = ("Spanish, QWERTY", () => new ESStandardLayout()),
        ["gb"] = ("English (UK), QWERTY", () => new GBStandardLayout()),
        ["tr"] = ("Turkish, Q", () => new TRStandardLayout()),
        ["dvorak"] = ("English (US), Dvorak", () => new USDvorakLayout()),
    };

    private static readonly Dictionary<string, string> s_aliases = new() { ["uk"] = "gb", ["de-latin1"] = "de", ["us-dvorak"] = "dvorak" };

    public static string Current { get; private set; } = "us";

    public static IEnumerable<(string Name, string Description)> All
    {
        get
        {
            foreach (var (name, layout) in s_layouts)
            {
                yield return (name, layout.Description);
            }
        }
    }

    /// <summary>Switches the active layout; returns false for an unknown name.</summary>
    public static bool Apply(string name)
    {
        name = name.ToLowerInvariant();
        if (s_aliases.TryGetValue(name, out string? canonical))
        {
            name = canonical;
        }

        if (!s_layouts.TryGetValue(name, out var layout))
        {
            return false;
        }

        KeyboardManager.SetKeyLayout(layout.Create());
        Current = name;
        return true;
    }

    /// <summary>Saves the layout to /etc/vconsole.conf, keeping any other settings in the file.</summary>
    public static void Save(string name)
    {
        var lines = new List<string>();
        if (File.Exists(ConfigPath))
        {
            foreach (string line in File.ReadAllText(ConfigPath).Split('\n'))
            {
                if (line.Length > 0 && !line.StartsWith("KEYMAP="))
                {
                    lines.Add(line);
                }
            }
        }

        lines.Add("KEYMAP=" + name);
        File.WriteAllText(ConfigPath, string.Join("\n", lines) + "\n");
    }

    /// <summary>Applies the layout saved in /etc/vconsole.conf, if any. Called once at boot.</summary>
    public static void LoadSaved()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        foreach (string line in File.ReadAllText(ConfigPath).Split('\n'))
        {
            if (line.StartsWith("KEYMAP="))
            {
                string name = line.Substring("KEYMAP=".Length).Trim().Trim('"');
                Log.Write("keyboard", Apply(name) ? "layout " + Current : "unknown KEYMAP '" + name + "' in " + ConfigPath);
            }
        }
    }
}
