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

    public const int CheckboxSize = 20;
    /// <summary>A checkbox row: a rounded box, filled with the accent and an antialiased check mark
    /// when checked, then the label. <paramref name="row"/> is the whole clickable row.</summary>
    public static void Checkbox(Surface surface, Rect row, string label, bool isChecked, bool focused = false)
    {
        var box = new Rect(row.X, row.Y + (row.H - CheckboxSize) / 2, CheckboxSize, CheckboxSize);
        if (focused) { surface.StrokeRoundRect(box.Inflate(3), 8, Theme.Accent); }
        if (isChecked)
        {
            surface.FillRoundRect(box, 5, Theme.Accent | 0xff000000U);
            CheckMark(surface, box, Theme.OnAccent);
        }
        else
        {
            surface.FillRoundRect(box, 5, Theme.Raised | 0xff000000U);
            surface.StrokeRoundRect(box, 5, Theme.TextSecondary);
        }
        surface.DrawTextCentered(label, Fonts.Regular, Theme.TextBody, Theme.TextPrimary,
            new Rect(box.Right + 12, row.Y, row.W - CheckboxSize - 12, row.H), false);
    }

    // Two strokes, 2 px wide, with coverage from each pixel centre's distance to the nearer stroke.
    private static void CheckMark(Surface surface, Rect box, uint color)
    {
        float s = box.W / 20f;
        float ax = box.X + 5 * s, ay = box.Y + 10.5f * s, bx = box.X + 8.5f * s, by = box.Y + 14f * s, cx = box.X + 15 * s, cy = box.Y + 6.5f * s;
        float half = 1.1f * s;
        Rect clip = surface.Clip;
        for (int y = box.Y; y < box.Bottom; y++)
            for (int x = box.X; x < box.Right; x++)
            {
                if (!clip.Contains(x, y)) { continue; }
                float px = x + 0.5f, py = y + 0.5f;
                float d = MathF.Min(Distance(px, py, ax, ay, bx, by), Distance(px, py, bx, by, cx, cy));
                float coverage = Math.Clamp(half + 0.5f - d, 0f, 1f);
                if (coverage > 0) { surface.BlendPixel(y * surface.Width + x, color, (int)(coverage * 255)); }
            }
    }

    private static float Distance(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0f, 1f);
        float ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return MathF.Sqrt(ex * ex + ey * ey);
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
