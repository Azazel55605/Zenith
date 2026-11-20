using Zenith.Gui.Graphics;

namespace Zenith.Gui;

/// <summary>Snap zones for a window dragged by its title (docs/design/motion-additions-concept.svg): a screen edge
/// takes that half, the top edge maximizes, a corner takes that quarter. Pure geometry, so host tests check it.</summary>
internal static class WindowSnap
{
    public const int None = 0, Left = 1, Right = 2, Top = 3, TopLeft = 4, TopRight = 5, BottomLeft = 6, BottomRight = 7;
    public static readonly string[] Names = { "", "left", "right", "top", "top-left", "top-right", "bottom-left", "bottom-right" };
    /// <summary>How close to an edge the pointer must be, and how far along it a corner reaches.</summary>
    public const int Edge = 3, Corner = 64;

    /// <summary>The zone under the pointer at (x, y) for a window being dragged by its title, or None.</summary>
    internal static int ZoneAt(Rect work, int x, int y)
    {
        bool left = x <= work.X + Edge, right = x >= work.Right - 1 - Edge, top = y <= work.Y + Edge;
        bool upper = y <= work.Y + Corner, lower = y >= work.Bottom - 1 - Corner;
        bool nearLeft = x <= work.X + Corner, nearRight = x >= work.Right - 1 - Corner;
        if ((left && upper) || (top && nearLeft)) { return TopLeft; }
        if ((right && upper) || (top && nearRight)) { return TopRight; }
        if (left && lower) { return BottomLeft; }
        if (right && lower) { return BottomRight; }
        if (top) { return Top; }
        if (left) { return Left; }
        if (right) { return Right; }
        return None;
    }
    /// <summary>Where a window snapped into <paramref name="zone"/> lands in the work area.</summary>
    internal static Rect Target(Rect work, int zone)
    {
        int halfW = work.W / 2, halfH = work.H / 2;
        return zone switch
        {
            Left => new Rect(work.X, work.Y, halfW, work.H),
            Right => new Rect(work.X + halfW, work.Y, work.W - halfW, work.H),
            Top => work,
            TopLeft => new Rect(work.X, work.Y, halfW, halfH),
            TopRight => new Rect(work.X + halfW, work.Y, work.W - halfW, halfH),
            BottomLeft => new Rect(work.X, work.Y + halfH, halfW, work.H - halfH),
            BottomRight => new Rect(work.X + halfW, work.Y + halfH, work.W - halfW, work.H - halfH),
            _ => new Rect(0, 0, 0, 0),
        };
    }
}
