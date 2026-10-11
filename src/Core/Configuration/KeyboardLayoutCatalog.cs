using System;

namespace Zenith.Core.Configuration;

internal static class KeyboardLayoutCatalog
{
    public static readonly string[] Names = { "us", "de", "fr", "es", "gb", "tr", "dvorak" };
    public static string? Normalize(string name)
    {
        name = name.ToLowerInvariant();
        name = name switch { "uk" => "gb", "de-latin1" => "de", "us-dvorak" => "dvorak", _ => name };
        foreach (string known in Names) { if (name == known) { return name; } }
        return null;
    }
}
