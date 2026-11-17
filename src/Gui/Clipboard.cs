namespace Zenith.Gui;

/// <summary>The system clipboard: plain text shared by every window (Ctrl+C/V in the editor, Ctrl+Shift+C/V in the terminal,
/// calls 639/659 for native windows). Stored in <see cref="Zenith.Core.Processes.SessionClipboard"/>.</summary>
internal static class Clipboard
{
    public static string Text { get => Zenith.Core.Processes.SessionClipboard.Text; set => Zenith.Core.Processes.SessionClipboard.Text = value; }
}
