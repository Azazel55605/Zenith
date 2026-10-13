using System;
using System.Collections.Generic;
using Zenith.Core.Configuration;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Keyboard.ScanMaps;

namespace Zenith.Core.Input;

/// <summary>
/// Keyboard layouts by their Linux keymap names. The saved choice lives in
/// <c>/etc/vconsole.conf</c> (<c>KEYMAP=de</c>), as on systemd systems, and is applied at boot.
/// </summary>
internal static class KeyboardLayouts
{
    private static readonly Dictionary<string, (string Description, Func<ScanMapBase> Create)> s_layouts = new()
    {
        ["us"] = ("English (US), QWERTY", () => new USStandardLayout()),
        ["de"] = ("German, QWERTZ", () => new GermanKeyboardLayout()),
        ["fr"] = ("French, AZERTY", () => new FRStandardLayout()),
        ["es"] = ("Spanish, QWERTY", () => new ESStandardLayout()),
        ["gb"] = ("English (UK), QWERTY", () => new GBStandardLayout()),
        ["tr"] = ("Turkish, Q", () => new TRStandardLayout()),
        ["dvorak"] = ("English (US), Dvorak", () => new USDvorakLayout()),
    };

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
        string? canonical = KeyboardLayoutCatalog.Normalize(name);
        if (canonical is null) { return false; }
        name = canonical;

        if (!s_layouts.TryGetValue(name, out var layout))
        {
            return false;
        }

        KeyboardManager.SetKeyLayout(layout.Create());
        Current = name;
        return true;
    }

}
