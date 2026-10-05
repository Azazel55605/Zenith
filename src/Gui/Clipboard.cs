namespace Zenith.Gui;

/// <summary>The system clipboard: plain text shared by every window (Ctrl+C/V in the editor, Ctrl+Shift+C/V in the terminal).</summary>
internal static class Clipboard
{
    public static string Text { get; set; } = string.Empty;
}
