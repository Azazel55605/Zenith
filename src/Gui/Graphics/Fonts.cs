using System;
using System.Collections.Generic;

namespace MyOS.Gui.Graphics;

/// <summary>The shell's fonts, decoded once from <see cref="EmbeddedFonts"/>.</summary>
internal static class Fonts
{
    private static FontFace? s_regular;
    private static FontFace? s_semiBold;
    private static FontFace? s_mono;

    public static FontFace Regular => s_regular ?? throw new InvalidOperationException("Fonts.Load() not called");
    public static FontFace SemiBold => s_semiBold ?? throw new InvalidOperationException("Fonts.Load() not called");

    /// <summary>Monospace face for the terminal.</summary>
    public static FontFace Mono => s_mono ?? throw new InvalidOperationException("Fonts.Load() not called");

    public static void Load()
    {
        s_regular ??= new FontFace(Convert.FromBase64String(EmbeddedFonts.InterRegular));
        s_semiBold ??= new FontFace(Convert.FromBase64String(EmbeddedFonts.InterSemiBold));
        s_mono ??= new FontFace(Convert.FromBase64String(EmbeddedFonts.HackRegular));
    }

    /// <summary>Splits <paramref name="text"/> into lines no wider than <paramref name="maxWidth"/>, breaking at spaces where possible.</summary>
    public static List<string> Wrap(string text, FontFace font, int size, int maxWidth)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            string line = string.Empty;
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureString(candidate, size) <= maxWidth)
                {
                    line = candidate;
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add(line);
                }

                // A single word wider than the line is hard-broken by character.
                line = word;
                while (line.Length > 1 && font.MeasureString(line, size) > maxWidth)
                {
                    int cut = line.Length - 1;
                    while (cut > 1 && font.MeasureString(line.Substring(0, cut), size) > maxWidth)
                    {
                        cut--;
                    }

                    lines.Add(line.Substring(0, cut));
                    line = line.Substring(cut);
                }
            }

            lines.Add(line);
        }

        return lines;
    }
}
