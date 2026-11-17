namespace Zenith.Core.Processes;

/// <summary>The session's clipboard text, shared by built-in windows (Zenith.Gui.Clipboard), terminals and native
/// windows (calls 639/659). Cleared when the session ends.</summary>
internal static class SessionClipboard
{
    public static string Text { get; set; } = string.Empty;
}
