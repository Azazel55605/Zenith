using Zenith.Gui.Graphics;
namespace Zenith.Gui.Shell;

/// <summary>Trusted recovery notice; never substitutes for a rejected executable.</summary>
internal sealed class AppLaunchNotice(string message) : Window("Application", 480, 170)
{
    public override bool CanResize => false;
    public override void DrawContent(Surface surface, Rect content)
    {
        surface.DrawText("Unable to open application", Fonts.SemiBold, Theme.TextHeading,
            Theme.TextPrimary, content.X + 20, content.Y + 20);
        surface.DrawText(message, Fonts.Regular, Theme.TextSmall,
            Theme.TextSecondary, content.X + 20, content.Y + 64);
    }
}
