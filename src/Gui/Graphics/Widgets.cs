namespace Zenith.Gui.Graphics;

internal enum WidgetStyle { Rounded = 0, Soft = 1, Sharp = 2 }
internal enum ButtonStyle { Filled = 0, Tonal = 1, Outline = 2 }

/// <summary>The shared widget and button styles (docs/design/appearance-concept.svg, kernel 0.0.72): Rounded (8 px
/// controls), Soft (larger radii) and Sharp (2 px corners); Filled, Tonal and Outline primary buttons. The desktop's own
/// controls follow here; native apps follow through the toolkit, which reads the same settings (call 669).</summary>
internal static class Widgets
{
    public static WidgetStyle? Override;
    public static WidgetStyle Current
    {
        get
        {
            if (Override is WidgetStyle forced) { return forced; }
            uint v = Zenith.Core.Configuration.StyleCatalog.Value(11);
            return v <= 2 ? (WidgetStyle)v : WidgetStyle.Rounded;
        }
    }
    public static ButtonStyle Buttons
    {
        get { uint v = Zenith.Core.Configuration.StyleCatalog.Value(12); return v <= 2 ? (ButtonStyle)v : ButtonStyle.Filled; }
    }
    /// <summary>A corner radius in the current style: Soft is about 1.6 times as round, Sharp at most 2 px. Circles and
    /// pills (a radius of half the height or more) stay round in every style.</summary>
    public static int Radius(int radius, int height = int.MaxValue) => Radius(Current, radius, height);
    public static int Radius(WidgetStyle style, int radius, int height = int.MaxValue)
    {
        if (radius <= 0 || (long)radius * 2 >= height) { return radius; }
        return style switch { WidgetStyle.Soft => System.Math.Min(radius * 8 / 5, height / 2), WidgetStyle.Sharp => System.Math.Min(radius, 2), _ => radius };
    }
}
