using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Core.Configuration;

/// <summary>Immutable semantic colors. Resource overrides are versioned, bounded and contrast checked.</summary>
internal sealed class AppearancePalette
{
    public static readonly string[] Roles = { "Surface", "Raised", "Subtle", "Border", "Text", "Secondary", "Accent", "OnAccent" };
    private readonly uint[] _colors;
    public bool Packaged { get; }
    private AppearancePalette(uint[] colors, bool packaged) { _colors = colors; Packaged = packaged; }
    public uint Get(uint mode, uint accent, int role) => _colors[((int)mode * 6 + (int)accent) * 8 + role];
    public static readonly AppearancePalette Default = CreateDefault();
    private static AppearancePalette CreateDefault()
    {
        uint[][] bases = { new uint[] { 0x202125, 0x2a2c31, 0x25262b, 0x45474f, 0xf1f2f4, 0xb4b7c0 },
            new uint[] { 0xfafafb, 0xffffff, 0xeff0f3, 0xcfd1d8, 0x202126, 0x5c606b } };
        uint[][] accents = { new uint[] { 0x8baaff, 0xb6a0ff, 0xf39db8, 0xe7bb70, 0x8bcba4, 0x7ecac7 },
            new uint[] { 0x3157b7, 0x7042be, 0xae325d, 0x886010, 0x277347, 0x197571 } };
        var colors = new uint[96];
        for (int mode = 0; mode < 2; mode++) { for (int a = 0; a < 6; a++)
        {
            int off = (mode * 6 + a) * 8;
            Array.Copy(bases[mode], 0, colors, off, 6); colors[off + 6] = accents[mode][a];
            colors[off + 7] = mode == 0 ? 0x202125U : 0xffffffU;
        } }
        return new(colors, false);
    }
    public static AppearancePalette Read(string? text)
    {
        if (text is null) { return Default; }
        try
        {
            if (text.Length > 4096 || text.Contains('\0')) { return Default; }
            uint[] colors = (uint[])Default._colors.Clone(); var seen = new HashSet<string>(); bool version = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) { continue; }
                int eq = line.IndexOf('='); if (eq < 1 || !seen.Add(line[..eq])) { return Default; }
                string key = line[..eq], value = line[(eq + 1)..];
                if (key == "VERSION") { if (value != "1") { return Default; } version = true; continue; }
                string[] parts = key.Split('.');
                if (parts.Length != 2 || !AppearanceService.TryMode(parts[0], out uint mode) || value.Length != 6
                    || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color)) { return Default; }
                int role = Array.IndexOf(Roles, parts[1]);
                if (role >= 0 && role != 6)
                { for (int a = 0; a < 6; a++) { colors[((int)mode * 6 + a) * 8 + role] = color; } }
                else if (AppearanceService.TryAccent(parts[1], out uint accent)) { colors[((int)mode * 6 + (int)accent) * 8 + 6] = color; }
                else { return Default; }
            }
            if (!version) { return Default; }
            for (int n = 0; n < 12; n++)
            {
                int o = n * 8;
                for (int surface = 0; surface < 3; surface++)
                {
                    if (Contrast(colors[o + surface], colors[o + 4]) < 4.5 || Contrast(colors[o + surface], colors[o + 5]) < 4.5
                        || Contrast(colors[o + surface], colors[o + 6]) < 3) { return Default; }
                }
                if (Contrast(colors[o + 6], colors[o + 7]) < 4.5) { return Default; }
            }
            return new(colors, true);
        }
        catch { return Default; }
    }
    private static double Luminance(uint c)
    {
        double Channel(int shift) { double v = ((c >> shift) & 255) / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(16) + .7152 * Channel(8) + .0722 * Channel(0);
    }
    private static double Contrast(uint a, uint b) => (Math.Max(Luminance(a), Luminance(b)) + .05) / (Math.Min(Luminance(a), Luminance(b)) + .05);
}
