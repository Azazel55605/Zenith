using System;

namespace Zenith.Gui.Graphics;

/// <summary>Shared solid fallback for trusted system/recovery windows. Role-specific
/// painters keep foreground controls separate from future host panel materials.</summary>
internal static class AppStyle
{
    public const int Inset = 24;
    public const int ControlHeight = 36;
    public static void Panel(Surface surface, Rect bounds)
        => surface.FillRoundRect(bounds, Theme.ControlRadius, Theme.Panel | 0xff000000U);

    public static void Button(Surface surface, Rect bounds, string label, bool primary = false, bool enabled = true, bool danger = false)
    {
        uint fill = primary && enabled ? Theme.Accent : enabled ? Theme.Raised : Theme.Panel;
        surface.FillRoundRect(bounds, Theme.ControlRadius, fill | 0xff000000U);
        if (enabled && !primary) { surface.StrokeRoundRect(bounds, Theme.ControlRadius, danger ? Theme.Danger : Theme.WindowBorder); }
        surface.DrawTextCentered(label, Fonts.SemiBold, Theme.TextBody,
            !enabled ? Theme.TextSecondary : primary ? Theme.OnAccent : Theme.TextPrimary, bounds, true);
    }

    public static void Field(Surface surface, Rect bounds, string value, bool focused)
    {
        surface.FillRoundRect(bounds, Theme.ControlRadius, Theme.Raised);
        surface.StrokeRoundRect(bounds, Theme.ControlRadius, focused ? Theme.Accent : Theme.WindowBorder);
        Rect clip = surface.Clip;
        surface.Clip = bounds.Inflate(-8).Intersect(clip);
        surface.DrawTextCentered(value, Fonts.Regular, Theme.TextBody, Theme.TextPrimary,
            new Rect(bounds.X + 12, bounds.Y, bounds.W - 24, bounds.H), false);
        surface.Clip = clip;
    }

    public static void NavigationRow(Surface surface, Rect bounds, string label, bool selected)
    {
        if (selected)
        {
            surface.FillRoundRect(bounds, Theme.ControlRadius, Theme.AccentSoft);
            surface.FillRoundRect(new Rect(bounds.X + 1, bounds.Y + 7, 3, bounds.H - 14), 1, Theme.Accent);
        }
        surface.DrawTextCentered(label, Fonts.Regular, Theme.TextBody, Theme.TextPrimary,
            new Rect(bounds.X + 12, bounds.Y, bounds.W - 24, bounds.H), false);
    }
}
