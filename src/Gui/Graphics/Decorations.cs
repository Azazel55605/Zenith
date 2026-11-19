using System;

namespace Zenith.Gui.Graphics;

internal enum DecorationStyle { Zenith = 0, Classic = 1, Dots = 2, Ribbon = 3 }

/// <summary>The trusted title bar in four styles (docs/design/decorations-concept.svg, kernel 0.0.71): one service, one
/// parameter set per style. The desktop draws it for every window, native or its own; programs cannot draw chrome.</summary>
internal static class Decorations
{
    /// <summary>Set by the host tests and the picker previews; otherwise the window decoration setting (style field 10).</summary>
    public static DecorationStyle? Override;
    public static DecorationStyle Current
    {
        get
        {
            if (Override is DecorationStyle forced) { return forced; }
            uint v = Zenith.Core.Configuration.SystemConfiguration.Settings?.Style(10) ?? 0;
            return v <= 3 ? (DecorationStyle)v : DecorationStyle.Zenith;
        }
    }
    public static int BarHeight(DecorationStyle s) => s switch { DecorationStyle.Classic => 32, DecorationStyle.Dots => 38, DecorationStyle.Ribbon => 34, _ => 44 };
    public static int Radius(DecorationStyle s) => s switch { DecorationStyle.Classic => 6, DecorationStyle.Ribbon => 14, _ => 12 };

    /// <summary>Close, maximize and minimize (empty when the window cannot resize) for a window at <paramref name="b"/>.</summary>
    public static (Rect Close, Rect Maximize, Rect Minimize) Buttons(DecorationStyle s, Rect b, bool canResize)
    {
        int bar = BarHeight(s);
        Rect close, max, min;
        switch (s)
        {
            case DecorationStyle.Classic:
                close = new Rect(b.Right - 46, b.Y, 46, bar);
                max = canResize ? close.Offset(-46, 0) : new Rect(0, 0, 0, 0);
                min = close.Offset(canResize ? -92 : -46, 0);
                break;
            case DecorationStyle.Dots:
                // 12 px dots, 20 px targets: close, minimize, maximize from the left.
                close = new Rect(b.X + 10, b.Y + (bar - 20) / 2, 20, 20);
                min = close.Offset(20, 0);
                max = canResize ? close.Offset(40, 0) : new Rect(0, 0, 0, 0);
                break;
            case DecorationStyle.Ribbon:
                close = new Rect(b.Right - 12 - 28, b.Y + (bar - 24) / 2, 28, 24);
                max = canResize ? close.Offset(-28, 0) : new Rect(0, 0, 0, 0);
                min = close.Offset(canResize ? -56 : -28, 0);
                break;
            default:
                close = new Rect(b.Right - Window.ButtonSize - 6, b.Y + (bar - Window.ButtonSize) / 2, Window.ButtonSize, Window.ButtonSize);
                max = canResize ? close.Offset(-Window.ButtonSize - 4, 0) : new Rect(0, 0, 0, 0);
                min = close.Offset(-(Window.ButtonSize + 4) * (canResize ? 2 : 1), 0);
                break;
        }
        return (close, max, min);
    }

    /// <summary>Where the title goes, and whether it is centered.</summary>
    public static (Rect Box, bool Centered) TitleBox(DecorationStyle s, Rect b, bool canResize)
    {
        var (close, max, min) = Buttons(s, b, canResize);
        int bar = BarHeight(s);
        switch (s)
        {
            case DecorationStyle.Dots:
            {
                int left = (canResize ? max : min).Right + 12, inset = left - b.X;
                return (new Rect(b.X + inset, b.Y, Math.Max(0, b.W - 2 * inset), bar), true);
            }
            case DecorationStyle.Ribbon:
            {
                int inset = b.Right - min.X + 8;
                return (new Rect(b.X + inset, b.Y, Math.Max(0, b.W - 2 * inset), bar), true);
            }
            default:
                return (new Rect(b.X + 14, b.Y, Math.Max(0, min.X - b.X - 20), bar), false);
        }
    }

    /// <summary>Paints the bar's controls and title (the bar's material or fill is already painted).</summary>
    public static void DrawBar(Surface surface, DecorationStyle s, Rect b, string title, bool focused, bool canResize, bool maximized, int mouseX, int mouseY)
    {
        var (close, max, min) = Buttons(s, b, canResize);
        var (box, centered) = TitleBox(s, b, canResize);
        uint text = focused ? Theme.TextPrimary : Theme.TextSecondary;
        Rect saved = surface.Clip;
        surface.Clip = box.Intersect(saved);
        if (centered) { surface.DrawTextCentered(title, Fonts.SemiBold, Theme.TextTitle, text, box, true); }
        else { surface.DrawTextCentered(title, Fonts.SemiBold, Theme.TextTitle, text, box, false); }
        surface.Clip = saved;
        switch (s)
        {
            case DecorationStyle.Classic:
                Square(surface, min, Glyph.Minimize, mouseX, mouseY, Theme.Hover, false, focused);
                if (canResize) { Square(surface, max, maximized ? Glyph.Restore : Glyph.Maximize, mouseX, mouseY, Theme.Hover, false, focused); }
                Square(surface, close, Glyph.Close, mouseX, mouseY, 0xFFC42B1Cu, true, focused);
                break;
            case DecorationStyle.Dots:
            {
                bool group = new Rect(close.X, close.Y, (canResize ? max : min).Right - close.X, close.H).Contains(mouseX, mouseY);
                Dot(surface, close, focused ? 0xFFEC6A5Eu : Theme.WindowBorder, group ? Glyph.Close : (Glyph?)null);
                Dot(surface, min, focused ? 0xFFF4BF4Fu : Theme.WindowBorder, group ? Glyph.Minimize : (Glyph?)null);
                if (canResize) { Dot(surface, max, focused ? 0xFF61C554u : Theme.WindowBorder, group ? Glyph.Maximize : (Glyph?)null); }
                break;
            }
            case DecorationStyle.Ribbon:
            {
                Rect pill = new(min.X - 4, close.Y, close.Right - min.X + 8, close.H);
                if (focused) { surface.FillRoundRect(pill, pill.H / 2, Theme.Hover); } else { surface.StrokeRoundRect(pill, pill.H / 2, Theme.WindowBorder); }
                Round(surface, min, Glyph.Minimize, mouseX, mouseY, Theme.Pressed, focused);
                if (canResize) { Round(surface, max, maximized ? Glyph.Restore : Glyph.Maximize, mouseX, mouseY, Theme.Pressed, focused); }
                Round(surface, close, Glyph.Close, mouseX, mouseY, Theme.Danger, focused);
                break;
            }
            default:
                Round(surface, min, Glyph.Minimize, mouseX, mouseY, Theme.Hover, focused, Theme.ControlRadius);
                if (canResize) { Round(surface, max, maximized ? Glyph.Restore : Glyph.Maximize, mouseX, mouseY, Theme.Hover, focused, Theme.ControlRadius); }
                Round(surface, close, Glyph.Close, mouseX, mouseY, Theme.Danger, focused, Theme.ControlRadius);
                break;
        }
    }

    /// <summary>The focused window's outline: Ribbon marks focus with an accent hairline instead of a darker bar.</summary>
    public static uint Border(DecorationStyle s, bool focused) => s == DecorationStyle.Ribbon && focused ? Theme.Accent : focused ? Theme.WindowBorderFocused : Theme.WindowBorder;
    /// <summary>Whether the bar keeps a fill of its own (Ribbon's window is one surface).</summary>
    public static bool BarFill(DecorationStyle s) => s != DecorationStyle.Ribbon;

    private enum Glyph { Minimize, Maximize, Restore, Close }

    private static void Round(Surface surface, Rect r, Glyph g, int mouseX, int mouseY, uint hover, bool focused, int radius = -1)
    {
        bool hot = r.Contains(mouseX, mouseY);
        if (hot) { surface.FillRoundRect(r, radius < 0 ? r.H / 2 : radius, hover); }
        Draw(surface, r, g, hot ? Theme.TextPrimary : focused ? Theme.TextSecondary : Theme.TextMuted);
    }
    private static void Square(Surface surface, Rect r, Glyph g, int mouseX, int mouseY, uint hover, bool danger, bool focused)
    {
        bool hot = r.Contains(mouseX, mouseY);
        if (hot) { surface.FillRect(r, hover); }
        Draw(surface, r, g, hot && danger ? 0xFFFFFFFFu : hot ? Theme.TextPrimary : focused ? Theme.TextPrimary : Theme.TextMuted);
    }
    private static void Dot(Surface surface, Rect target, uint color, Glyph? g)
    {
        Rect dot = new(target.X + (target.W - 12) / 2, target.Y + (target.H - 12) / 2, 12, 12);
        surface.FillRoundRect(dot, 6, color);
        if (g is Glyph glyph) { DrawSmall(surface, dot, glyph, 0xFF5A1A14u); }
    }

    private static void Draw(Surface surface, Rect r, Glyph g, uint color)
    {
        int cx = r.X + r.W / 2, cy = r.Y + r.H / 2;
        switch (g)
        {
            case Glyph.Minimize: surface.FillRect(new Rect(cx - 5, cy, 10, 1), color); break;
            case Glyph.Maximize: surface.StrokeRoundRect(new Rect(cx - 5, cy - 5, 10, 10), 2, color); break;
            case Glyph.Restore:
                surface.StrokeRoundRect(new Rect(cx - 6, cy - 3, 9, 9), 2, color);
                surface.FillRect(new Rect(cx - 3, cy - 6, 8, 1), color);
                surface.FillRect(new Rect(cx + 5, cy - 6, 1, 8), color);
                break;
            default:
                for (int i = -4; i <= 4; i++) { surface.FillRect(new Rect(cx + i, cy + i, 1, 1), color); surface.FillRect(new Rect(cx + i, cy - i, 1, 1), color); }
                break;
        }
    }
    private static void DrawSmall(Surface surface, Rect dot, Glyph g, uint color)
    {
        int cx = dot.X + 6, cy = dot.Y + 6;
        switch (g)
        {
            case Glyph.Minimize: surface.FillRect(new Rect(cx - 3, cy, 6, 1), color); break;
            case Glyph.Maximize: surface.FillRect(new Rect(cx - 3, cy, 7, 1), color); surface.FillRect(new Rect(cx, cy - 3, 1, 7), color); break;
            default: for (int i = -2; i <= 2; i++) { surface.FillRect(new Rect(cx + i, cy + i, 1, 1), color); surface.FillRect(new Rect(cx + i, cy - i, 1, 1), color); } break;
        }
    }
}
