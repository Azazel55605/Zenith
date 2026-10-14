using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Gui.Graphics;

/// <summary>
/// Design tokens for the shell. Colors are packed ARGB (0xAARRGGBB); an alpha
/// below 0xFF is blended over whatever is already drawn.
/// </summary>
internal static class Theme
{
    // Surfaces
    public static uint WindowBackground => s_colors["WindowBackground"];
    public static uint WindowBorder => s_colors["WindowBorder"];
    public static uint WindowBorderFocused => s_colors["WindowBorderFocused"];
    public static uint TitleDivider => s_colors["TitleDivider"];
    public static uint Panel => s_colors["Panel"];
    public static uint PanelBorder => s_colors["PanelBorder"];
    public static uint Menu => s_colors["Menu"];

    // Interaction
    public static uint Accent => s_colors["Accent"];
    public static uint AccentSoft => s_colors["AccentSoft"];
    public static uint Hover => s_colors["Hover"];
    public static uint Pressed => s_colors["Pressed"];
    public static uint Danger => s_colors["Danger"];

    // Text
    public static uint TextPrimary => s_colors["TextPrimary"];
    public static uint TextSecondary => s_colors["TextSecondary"];
    public static uint TextMuted => s_colors["TextMuted"];

    // Shape
    public const int WindowRadius = 10;
    public const int ControlRadius = 6;
    public const int ShadowSize = 20;
    public const int ShadowAlpha = 90;

    // Type scale (pixel sizes)
    public const int TextSmall = 12;
    public const int TextBody = 14;
    public const int TextTitle = 13;
    public const int TextHeading = 22;

    private static Dictionary<string, uint> Defaults() => new(StringComparer.Ordinal)
    {
        ["WindowBackground"] = 0xFF1C2030,
        ["WindowBorder"] = 0x1FFFFFFF,
        ["WindowBorderFocused"] = 0x5C7C9CFF,
        ["TitleDivider"] = 0x14FFFFFF,
        ["Panel"] = 0xE0141724,
        ["PanelBorder"] = 0x1AFFFFFF,
        ["Menu"] = 0xF61A1E2C,
        ["Accent"] = 0xFF7C9CFF,
        ["AccentSoft"] = 0x337C9CFF,
        ["Hover"] = 0x14FFFFFF,
        ["Pressed"] = 0x24FFFFFF,
        ["Danger"] = 0xFFE5484D,
        ["TextPrimary"] = 0xFFE8EAF2,
        ["TextSecondary"] = 0xFF9AA1B5,
        ["TextMuted"] = 0xFF6B7287,
    };
    private static Dictionary<string, uint> s_colors = Defaults();
    public static string Name { get; private set; } = "Zenith recovery";
    public static bool Apply(string? text)
    {
        var colors = Defaults(); string name = "Zenith recovery";
        try
        {
            if (text is not null)
            {
                if (text.Length > 4096 || text.IndexOf('\0') >= 0 || !text.StartsWith("VERSION=1\n", StringComparison.Ordinal)) { throw new FormatException(); }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in text.Split('\n'))
                {
                    if (line.Length == 0 || line.StartsWith('#')) { continue; }
                    int equal = line.IndexOf('=');
                    if (equal <= 0 || !seen.Add(line[..equal])) { throw new FormatException(); }
                    string key = line[..equal], value = line[(equal + 1)..];
                    if (key == "VERSION") { if (value != "1") { throw new FormatException(); } }
                    else if (key == "NAME")
                    {
                        if (value.Length is < 1 or > 40) { throw new FormatException(); }
                        foreach (char c in value) { if (c < 32 || c > 126) { throw new FormatException(); } }
                        name = value;
                    }
                    else
                    {
                        if (!colors.ContainsKey(key) || value.Length != 8 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color)) { throw new FormatException(); }
                        colors[key] = color;
                    }
                }
                foreach (string key in new[] { "WindowBackground", "TextPrimary", "TextSecondary", "TextMuted", "Accent", "Danger" })
                { if ((colors[key] >> 24) != 255) { throw new FormatException(); } }
                if (Contrast(colors["WindowBackground"], colors["TextPrimary"]) < 4.5 || Contrast(colors["WindowBackground"], colors["TextSecondary"]) < 3
                    || Contrast(colors["WindowBackground"], colors["Accent"]) < 3) { throw new FormatException(); }
            }
            s_colors = colors; Name = name; return true;
        }
        catch { s_colors = Defaults(); Name = "Zenith recovery"; return false; }
    }
    private static double Light(uint color)
    {
        double Channel(int shift)
        {
            double v = ((color >> shift) & 255) / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
    }
    private static double Contrast(uint a, uint b) => (Math.Max(Light(a), Light(b)) + 0.05) / (Math.Min(Light(a), Light(b)) + 0.05);
}
