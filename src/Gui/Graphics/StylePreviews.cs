using System;

namespace Zenith.Gui.Graphics;

/// <summary>Pictures for Meridian's pickers (call 669 preview, kernel 0.0.71), drawn by the desktop with the same code
/// that draws the real thing, so a preview never differs from the result.</summary>
internal static class StylePreviews
{
    public static int[]? Render(uint field, uint index, int w, int h)
    {
        return field switch
        {
            8 => CursorPack((int)index, w, h),
            9 => CursorSize((int)index, w, h),
            10 => Decoration((DecorationStyle)Math.Min(3u, index), w, h),
            11 => WidgetSample((WidgetStyle)Math.Min(2u, index), Widgets.Buttons, w, h),
            12 => WidgetSample(Widgets.Current, (ButtonStyle)Math.Min(2u, index), w, h),
            13 => Splash((int)Math.Min(3u, index), w, h),
            14 => Login((int)Math.Min(3u, index), w, h),
            15 => Boot((int)Math.Min(3u, index), w, h),
            17 => IconSet(Math.Min(2u, index), w, h),
            _ => null,
        };
    }

    private static void Fill(int[] p, int w, int x0, int y0, int x1, int y1, uint rgb)
    {
        int c = unchecked((int)(0xFF000000 | rgb));
        for (int y = Math.Max(0, y0); y < y1; y++) { for (int x = Math.Max(0, x0); x < x1; x++) { if (y * w + x < p.Length) { p[y * w + x] = c; } } }
    }

    /// <summary>Blends a premultiplied square image over opaque pixels.</summary>
    public static void Blend(int[] dst, int w, int h, uint[] src, int size, int left, int top)
    {
        for (int y = 0; y < size; y++)
        {
            int py = top + y; if (py < 0 || py >= h) { continue; }
            for (int x = 0; x < size; x++)
            {
                int px = left + x; if (px < 0 || px >= w) { continue; }
                uint s = src[y * size + x], a = s >> 24; if (a == 0) { continue; }
                uint d = unchecked((uint)dst[py * w + px]), k = 255 - a;
                uint r = Math.Min(255u, ((s >> 16) & 255) + ((d >> 16) & 255) * k / 255);
                uint g = Math.Min(255u, ((s >> 8) & 255) + ((d >> 8) & 255) * k / 255);
                uint b = Math.Min(255u, (s & 255) + (d & 255) * k / 255);
                dst[py * w + px] = unchecked((int)(0xFF000000 | r << 16 | g << 8 | b));
            }
        }
    }

    private static Surface Backdrop(int w, int h)
    {
        var surface = new Surface(w, h);
        Wallpaper.Render(surface, Zenith.Core.Configuration.SystemConfiguration.Settings?.Current.Wallpaper ?? 0, Theme.IsLight, Theme.Accent & 0xFFFFFF, quiet: true);
        return surface;
    }

    /// <summary>Two windows in one decoration: an inactive one behind, the focused one in front.</summary>
    private static int[] Decoration(DecorationStyle style, int w, int h)
    {
        var s = Backdrop(w, h);
        int bar = Decorations.BarHeight(style), radius = Decorations.Radius(style);
        void Frame(Rect b, string title, bool focused)
        {
            s.FillRoundRect(b, radius, Theme.WindowBackground | 0xFF000000u);
            if (Decorations.BarFill(style)) { s.FillRoundRect(new Rect(b.X, b.Y, b.W, bar + radius), radius, Theme.Panel | 0xFF000000u); s.FillRect(new Rect(b.X, b.Y + bar, b.W, radius), Theme.WindowBackground | 0xFF000000u); }
            Decorations.DrawBar(s, style, b, title, focused, true, false, -1, -1);
            s.StrokeRoundRect(b, radius, Decorations.Border(style, focused));
            s.FillRoundRect(new Rect(b.X + 10, b.Y + bar + 10, Math.Max(0, b.W / 3 - 10), Math.Max(0, b.H - bar - 20)), 6, Theme.Panel | 0xFF000000u);
        }
        Frame(new Rect(w / 14, h / 9, w * 7 / 10, h * 6 / 10), "Files", false);
        Frame(new Rect(w * 2 / 9, h * 3 / 10, w * 3 / 4, h * 66 / 100), "Documents", true);
        return s.Pixels;
    }

    /// <summary>A small login screen: the card in the middle of the wallpaper.</summary>
    private static int[] LoginCard(int w, int h)
    {
        var s = Backdrop(w, h);
        Rect card = new(w * 3 / 10, h / 4, w * 2 / 5, h / 2);
        s.FillRoundRect(card, 6, Theme.WindowBackground | 0xFF000000u);
        s.FillRoundRect(new Rect(card.X + card.W / 2 - 6, card.Y + 8, 12, 12), 6, Theme.Accent | 0xFF000000u);
        s.FillRoundRect(new Rect(card.X + 8, card.Bottom - 18, card.W - 16, 9), 3, Theme.Raised | 0xFF000000u);
        return s.Pixels;
    }
    private static int[] DesktopMini(int w, int h)
    {
        var s = Backdrop(w, h);
        s.FillRoundRect(new Rect(w / 10, h / 8, w * 9 / 20, h * 11 / 20), 4, Theme.WindowBackground | 0xFF000000u);
        s.FillRoundRect(new Rect(w * 9 / 20, h / 4, w * 2 / 5, h * 9 / 20), 4, Theme.WindowBackground | 0xFF000000u);
        s.FillRoundRect(new Rect(w / 5, h - h / 7, w * 3 / 5, h / 10), 3, Theme.Panel | 0xFF000000u);
        return s.Pixels;
    }

    /// <summary>The middle of the transition from the login card to the desktop, in the splash style.</summary>
    private static int[] Splash(int style, int w, int h)
    {
        int[] from = LoginCard(w, h), to = DesktopMini(w, h), output = new int[w * h];
        int[]? greeting = style == Zenith.Gui.Shell.SplashStyles.Greeting ? Zenith.Gui.Shell.SplashStyles.GreetingCard(to, w, h, "Alex") : null;
        Zenith.Gui.Shell.SplashStyles.Frame(style, from, to, greeting, output, w, h, style == Zenith.Gui.Shell.SplashStyles.Greeting ? Zenith.Gui.Motion.MotionCurve.One * 45 / 100 : Zenith.Gui.Motion.MotionCurve.One * 45 / 100);
        return output;
    }

    /// <summary>A login style in miniature: card, account tiles, clock or split panel.</summary>
    private static int[] Login(int style, int w, int h)
    {
        var s = Backdrop(w, h);
        uint card = Theme.WindowBackground | 0xFF000000u, accent = Theme.Accent | 0xFF000000u, muted = 0xFF5C606Bu;
        switch (style)
        {
            case 1:
                for (int i = 0; i < 3; i++)
                {
                    int cx = w / 2 + (i - 1) * w / 6;
                    s.FillRoundRect(new Rect(cx - 9, h / 4, 18, 18), 9, i == 0 ? accent : muted);
                }
                s.FillRoundRect(new Rect(w * 3 / 10, h * 11 / 20, w * 2 / 5, h / 8), 4, card);
                s.StrokeRoundRect(new Rect(w * 3 / 10, h * 11 / 20, w * 2 / 5, h / 8), 4, accent);
                break;
            case 2:
                s.FillRect(new Rect(0, 0, w, h), 0x33000000U);
                s.DrawTextCentered("19:42", Fonts.Regular, Math.Max(12, h / 4), 0xFFF1F2F4U, new Rect(0, h / 4, w, h / 3), true);
                s.FillRoundRect(new Rect(w / 3, h * 3 / 4, w / 3, h / 10), h / 20, 0xCC202125U);
                break;
            case 3:
                s.FillRect(new Rect(w * 5 / 8, 0, w * 3 / 8, h), card);
                for (int i = 0; i < 3; i++) { s.FillRoundRect(new Rect(w * 5 / 8 + 8, h / 6 + i * h / 7, w * 3 / 8 - 16, h / 9), 3, i == 0 ? (Theme.AccentSoft | 0xFF000000u) : card); s.FillRoundRect(new Rect(w * 5 / 8 + 12, h / 6 + i * h / 7 + 3, h / 12, h / 12), h / 24, i == 0 ? accent : muted); }
                s.DrawText("19:42", Fonts.Regular, Math.Max(10, h / 6), 0xFFF1F2F4U, w / 12, h * 2 / 3);
                break;
            default: return LoginCard(w, h);
        }
        return s.Pixels;
    }

    /// <summary>Ten icons of a set, as drawn from its own files; a set that is not installed says so.</summary>
    private static int[] IconSet(uint set, int w, int h)
    {
        var s = new Surface(w, h);
        s.FillRect(new Rect(0, 0, w, h), Theme.WindowBackground | 0xFF000000u);
        if (!Icons.SetInstalled(set))
        {
            s.DrawTextCentered("Not installed", Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, new Rect(0, 0, w, h), true);
            return s.Pixels;
        }
        string[] names = { "house", "folder", "search", "download", "package", "palette", "network", "volume-2", "terminal", "shield" };
        int size = Math.Min(24, h / 3), gap = (w - 5 * size) / 6;
        Icons.ForcedSet = set;
        for (int i = 0; i < names.Length; i++)
        { Icons.Draw(s, names[i], gap + (i % 5) * (size + gap), h / 2 - size - 4 + (i / 5) * (size + 8), size, Theme.TextPrimary); }
        Icons.ForcedSet = null;
        return s.Pixels;
    }

    /// <summary>The boot screen in a style, three of five phases done.</summary>
    private static int[] Boot(int style, int w, int h)
    {
        // Drawn at a screen-like size, then scaled down (the boot screen never draws below its normal size).
        const int bw = 640, bh = 400;
        var big = new Surface(bw, bh);
        Zenith.Gui.Shell.BootScreen.Paint(big, "Network", 3 * 256 / 5, 100, style);
        var p = new int[w * h];
        for (int y = 0; y < h; y++) { for (int x = 0; x < w; x++) { p[y * w + x] = big.Pixels[(y * bh / h) * bw + x * bw / w]; } }
        return p;
    }

    /// <summary>A card with a field, a primary and a secondary button in a widget and button style.</summary>
    private static int[] WidgetSample(WidgetStyle widgets, ButtonStyle buttons, int w, int h)
    {
        var s = new Surface(w, h);
        s.FillRect(new Rect(0, 0, w, h), Theme.WindowBackground | 0xFF000000u);
        Rect card = new(8, 8, w - 16, h - 16);
        s.FillRoundRect(card, Widgets.Radius(widgets, 12, card.H), Theme.Panel | 0xFF000000u);
        s.StrokeRoundRect(card, Widgets.Radius(widgets, 12, card.H), Theme.WindowBorder);
        int bh = Math.Max(18, Math.Min(28, card.H / 3)), bw = (card.W - 30) / 2;
        Rect field = new(card.X + 10, card.Y + 10, card.W - 20, bh);
        s.FillRoundRect(field, Widgets.Radius(widgets, 8, bh), Theme.WindowBackground | 0xFF000000u);
        s.StrokeRoundRect(field, Widgets.Radius(widgets, 8, bh), Theme.WindowBorder);
        Rect primary = new(card.X + 10, card.Bottom - 10 - bh, bw, bh), secondary = new(primary.Right + 10, primary.Y, bw, bh);
        int r = Widgets.Radius(widgets, 8, bh);
        uint accent = Theme.Accent | 0xFF000000u;
        switch (buttons)
        {
            case ButtonStyle.Tonal: s.FillRoundRect(primary, r, Mix(Theme.Panel, accent, 64)); break;
            case ButtonStyle.Outline: s.StrokeRoundRect(primary, r, accent); break;
            default: s.FillRoundRect(primary, r, accent); break;
        }
        s.DrawTextCentered("Apply", Fonts.SemiBold, Theme.TextSmall, buttons == ButtonStyle.Filled ? 0xFF10302Fu : accent, primary, true);
        s.FillRoundRect(secondary, r, Theme.Hover | 0xFF000000u);
        s.DrawTextCentered("Cancel", Fonts.Regular, Theme.TextSmall, Theme.TextPrimary, secondary, true);
        return s.Pixels;
    }
    private static uint Mix(uint a, uint b, uint t)
    {
        uint r = (((a >> 16) & 255) * (255 - t) + ((b >> 16) & 255) * t) / 255, g = (((a >> 8) & 255) * (255 - t) + ((b >> 8) & 255) * t) / 255, bl = ((a & 255) * (255 - t) + (b & 255) * t) / 255;
        return 0xFF000000u | r << 16 | g << 8 | bl;
    }

    /// <summary>Arrow, link, text and busy of one pack, on the tile color the concept uses for it.</summary>
    private static int[] CursorPack(int pack, int w, int h)
    {
        var p = new int[w * h];
        bool darkBody = pack is CursorPacks.Dark or CursorPacks.ContrastBlack;
        Fill(p, w, 0, 0, w, h, darkBody ? 0xC9CED8u : 0x2E3B4Bu);
        int size = Math.Min(h * 2 / 3, Math.Max(16, w / 5));
        if (pack is CursorPacks.ContrastWhite or CursorPacks.ContrastBlack) { size = Math.Min(h * 4 / 5, size * 5 / 4); }
        CursorShape[] shapes = { CursorShape.Default, CursorShape.Pointer, CursorShape.Text, CursorShape.Wait };
        int gap = (w - shapes.Length * size) / (shapes.Length + 1);
        for (int i = 0; i < shapes.Length; i++)
        {
            uint[] image = CursorPacks.Get(pack, shapes[i], size, Theme.Accent & 0xFFFFFF, out _, out _);
            Blend(p, w, h, image, size, gap + i * (size + gap), (h - size) / 2);
        }
        return p;
    }

    /// <summary>The arrow of the current pack at one of the three sizes, at its real size when it fits.</summary>
    private static int[] CursorSize(int choice, int w, int h)
    {
        var p = new int[w * h];
        int pack = Cursor.Variant;
        bool darkBody = pack is CursorPacks.Dark or CursorPacks.ContrastBlack;
        Fill(p, w, 0, 0, w, h, darkBody ? 0xC9CED8u : 0x2E3B4Bu);
        int size = Math.Min(h - 8, CursorPacks.Logical(pack, choice));
        uint[] image = CursorPacks.Get(pack, CursorShape.Default, size, Theme.Accent & 0xFFFFFF, out _, out _);
        Blend(p, w, h, image, size, (w - size) / 2, (h - size) / 2);
        return p;
    }
}
