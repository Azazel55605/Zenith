namespace Zenith.Gui.Graphics;

/// <summary>An arrow pointer with a dark outline and a faint drop shadow, hotspot at its tip.</summary>
internal static class Cursor
{
    // '#' outline, '.' fill, ' ' transparent.
    private static readonly string[] s_shape =
    {
        "#           ",
        "##          ",
        "#.#         ",
        "#..#        ",
        "#...#       ",
        "#....#      ",
        "#.....#     ",
        "#......#    ",
        "#.......#   ",
        "#........#  ",
        "#.........# ",
        "#......#####",
        "#...#..#    ",
        "#..##..#    ",
        "#.#  #..#   ",
        "##   #..#   ",
        "#     #..#  ",
        "      #..#  ",
        "       ##   ",
    };

    private const uint Outline = 0xFF101320;
    private const uint Fill = 0xFFFFFFFF;
    private const uint Shadow = 0x40000000;

    /// <summary>The pixels the cursor (shadow included) can touch when drawn at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static Rect Bounds(int x, int y) => new(x, y, s_shape[0].Length + 1, s_shape.Length + 2);

    public static void Draw(Surface surface, int x, int y)
    {
        Plot(surface, x + 1, y + 2, Shadow, Shadow);
        Plot(surface, x, y, Outline, Fill);
    }

    private static void Plot(Surface surface, int x, int y, uint outline, uint fill)
    {
        for (int row = 0; row < s_shape.Length; row++)
        {
            string line = s_shape[row];
            for (int col = 0; col < line.Length; col++)
            {
                char c = line[col];
                if (c == '#')
                {
                    surface.FillRect(new Rect(x + col, y + row, 1, 1), outline);
                }
                else if (c == '.')
                {
                    surface.FillRect(new Rect(x + col, y + row, 1, 1), fill);
                }
            }
        }
    }
}
