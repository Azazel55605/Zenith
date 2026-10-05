namespace Zenith.Gui.Graphics;

/// <summary>
/// Design tokens for the shell. Colors are packed ARGB (0xAARRGGBB); an alpha
/// below 0xFF is blended over whatever is already drawn.
/// </summary>
internal static class Theme
{
    // Surfaces
    public const uint WindowBackground = 0xFF1C2030;
    public const uint WindowBorder = 0x1FFFFFFF;
    public const uint WindowBorderFocused = 0x5C7C9CFF;
    public const uint TitleDivider = 0x14FFFFFF;
    public const uint Panel = 0xE0141724;
    public const uint PanelBorder = 0x1AFFFFFF;
    public const uint Menu = 0xF61A1E2C;

    // Interaction
    public const uint Accent = 0xFF7C9CFF;
    public const uint AccentSoft = 0x337C9CFF;
    public const uint Hover = 0x14FFFFFF;
    public const uint Pressed = 0x24FFFFFF;
    public const uint Danger = 0xFFE5484D;

    // Text
    public const uint TextPrimary = 0xFFE8EAF2;
    public const uint TextSecondary = 0xFF9AA1B5;
    public const uint TextMuted = 0xFF6B7287;

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
}
