using Cosmos.Kernel.System.Graphics;

namespace Zenith.Gui.Shell;

internal static class SessionDisplay
{
    private static Canvas? s_screen;
    public static Canvas Screen => s_screen ??= Canvas.GetFullScreen();
}
