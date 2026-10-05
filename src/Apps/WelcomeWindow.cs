using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

internal sealed class WelcomeWindow : Window
{
    private static readonly string[] s_tips =
    {
        "Open apps from the launcher in the bottom-left corner.",
        "Drag a window by its title bar; click a taskbar entry to minimize or restore it.",
        "Terminal gives you a Unix-style shell: try ls /, cat /etc/os-release, help.",
    };

    public WelcomeWindow() : base("Welcome", 480, 270)
    {
    }

    public override bool CanResize => false;

    public override void DrawContent(Surface surface, Rect content)
    {
        int x = content.X + 28;
        int y = content.Y + 24;
        int width = content.W - 56;

        int logo = Fonts.SemiBold.GetLineHeight(Theme.TextHeading);
        Logo.Draw(surface, new Rect(x, y, logo, logo));
        surface.DrawText("Welcome to Zenith", Fonts.SemiBold, Theme.TextHeading, Theme.TextPrimary, x + logo + 12, y);
        y += Fonts.SemiBold.GetLineHeight(Theme.TextHeading) + 6;

        foreach (string line in Fonts.Wrap("A small Unix-style operating system written in C# on Cosmos Gen 3.", Fonts.Regular, Theme.TextBody, width))
        {
            surface.DrawText(line, Fonts.Regular, Theme.TextBody, Theme.TextSecondary, x, y);
            y += Fonts.Regular.GetLineHeight(Theme.TextBody);
        }

        y += 18;
        int lineHeight = Fonts.Regular.GetLineHeight(Theme.TextBody);
        foreach (string tip in s_tips)
        {
            surface.FillRoundRect(new Rect(x, y + lineHeight / 2 - 3, 6, 6), 3, Theme.Accent);
            foreach (string line in Fonts.Wrap(tip, Fonts.Regular, Theme.TextBody, width - 20))
            {
                surface.DrawText(line, Fonts.Regular, Theme.TextBody, Theme.TextPrimary, x + 20, y);
                y += lineHeight;
            }

            y += 10;
        }
    }
}
