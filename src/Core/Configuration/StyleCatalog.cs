using System;
using System.Buffers.Binary;
using System.Text;

namespace Zenith.Core.Configuration;

/// <summary>The style catalogue (call 669, kernel 0.0.72, docs/components/APPEARANCE.md): every choice of a setting
/// with its label, kind and color, and preview pictures the desktop draws with its own code, so Meridian's pickers
/// show the real thing. Saving stays the system settings call (626) with the same field numbers.</summary>
internal static class StyleCatalog
{
    public const int ListBytes = 4096, EntryBytes = 64, MaximumEntries = 60;
    public const uint FieldWallpaper = 6, FieldCursor = 8;
    /// <summary>Renders wallpaper <c>choice</c> at width x height in the current mode and accent (set by the desktop).</summary>
    public static Func<uint, int, int, int[]>? RenderWallpaper;
    /// <summary>The current accent (RGB), for the "Accent color" swatch.</summary>
    public static Func<uint>? Accent;
    /// <summary>The current choice of a style field, for drawing code that host tests also compile (set at boot).</summary>
    public static Func<uint, uint>? Values;
    public static uint Value(uint field) => Values?.Invoke(field) ?? 0;
    /// <summary>Draws a preview of style choice <c>index</c> of <c>field</c> at width x height (cursors: a row of
    /// shapes; later decorations and startup styles), or null when the field has none. Set by the desktop.</summary>
    public static Func<uint, uint, int, int, int[]?>? RenderPreview;

    private static uint SolidColor(string name, uint accent) => name switch
    {
        "solid-graphite" => 0x202125, "solid-midnight" => 0x1B2036, "solid-forest" => 0x243B36, "solid-plum" => 0x3A2433,
        "solid-cream" => 0xF8F4EC, "solid-mist" => 0xE8EEF6, "solid-sand" => 0xEDE5D6, _ => accent,
    };

    /// <summary>The list for a field: version, field, count, current, revision, then 64-byte entries (name[24],
    /// label[32], kind, color). Kind for wallpapers: 0 dynamic, 1 solid. Null for a field without a catalogue.</summary>
    public static byte[]? List(SettingsService service, uint field, uint accent)
    {
        var (state, revision) = service.Snapshot();
        string[] names, labels; uint current;
        if (field == FieldWallpaper) { names = SettingsService.WallpaperNames; labels = SettingsService.WallpaperLabels; current = state.Wallpaper; }
        else if (field == FieldCursor) { names = SettingsService.CursorNames; labels = SettingsService.CursorLabels; current = state.Cursor; }
        else if (SettingsService.StyleFor(field) is SettingsService.StyleChoice style) { names = style.Names; labels = style.Labels; current = service.Style(field); }
        else { return null; }
        if (names.Length > MaximumEntries) { throw new InvalidOperationException("style catalogue exceeds ABI bounds"); }
        byte[] data = new byte[ListBytes]; var b = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1); BinaryPrimitives.WriteUInt32LittleEndian(b[4..], field);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], (uint)names.Length); BinaryPrimitives.WriteUInt32LittleEndian(b[12..], current);
        BinaryPrimitives.WriteUInt64LittleEndian(b[16..], revision);
        for (int i = 0; i < names.Length; i++)
        {
            int at = 32 + i * EntryBytes;
            Put(data, at, 24, names[i]); Put(data, at + 24, 32, labels[i].Replace(" (dynamic)", ""));
            bool solid = field == FieldWallpaper && names[i].StartsWith("solid-", StringComparison.Ordinal);
            BinaryPrimitives.WriteUInt32LittleEndian(b[(at + 56)..], solid ? 1U : 0U);
            BinaryPrimitives.WriteUInt32LittleEndian(b[(at + 60)..], solid ? SolidColor(names[i], accent) : 0U);
        }
        return data;
    }
    private static void Put(byte[] data, int offset, int capacity, string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value);
        if (text.Length >= capacity) { throw new InvalidOperationException("style name exceeds ABI bounds"); }
        text.CopyTo(data, offset);
    }
}
