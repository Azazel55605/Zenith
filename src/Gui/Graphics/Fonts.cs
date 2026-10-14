using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zenith.Core.Packages;

namespace Zenith.Gui.Graphics;

/// <summary>The shell's fonts, loaded from signed installed data with embedded recovery fallbacks.</summary>
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
        s_regular ??= LoadFont("Inter-Regular.ttf", EmbeddedFonts.InterRegular);
        s_semiBold ??= LoadFont("Inter-SemiBold.ttf", EmbeddedFonts.InterSemiBold);
        s_mono ??= LoadFont("Hack-Regular.ttf", EmbeddedFonts.HackRegular);
    }
    public static void Reload()
    {
        s_regular = null; s_semiBold = null; s_mono = null;
        Load();
    }

    private static FontFace LoadFont(string name, string fallback)
    {
        string path = "/usr/share/zenith/fonts/" + name;
        if (File.Exists(path))
        {
            try
            {
                var store = new NativePackageStore();
                var owned = new PackageManager(store).List().SelectMany(package => package.Manifest.Files).FirstOrDefault(file => file.Path == path);
                byte[] bytes = store.Read(path, PackageManifest.MaximumFileBytes);
                if (owned is null || owned.Length != bytes.Length || PackagePlanner.Digest(bytes) != owned.Digest || store.Mode(path) != owned.Mode)
                { throw new IOException("font differs from installed package"); }
                var face = new FontFace(bytes); Zenith.Core.Log.Write("fonts", "loaded packaged " + name); return face;
            }
            catch { Zenith.Core.Log.Write("fonts", "packaged font unavailable; using recovery " + name); }
        }
        return new FontFace(Convert.FromBase64String(fallback));
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
