namespace Zenith.Core.IO;

/// <summary>Device-independent VT input used only by a foreground raw-terminal lease.</summary>
internal static class TerminalKeyEncoding
{
    public static string Encode(string name, char character, bool control)
    {
        if (control && name.Length == 1 && name[0] is >= 'A' and <= 'Z') { return ((char)(name[0] - 'A' + 1)).ToString(); }
        return name switch
        {
            "Enter" => "\r",
            "Tab" => "\t",
            "Backspace" => "\x7f",
            "Escape" => "\x1b",
            "UpArrow" => "\x1b[A",
            "DownArrow" => "\x1b[B",
            "RightArrow" => "\x1b[C",
            "LeftArrow" => "\x1b[D",
            "Home" => "\x1b[H",
            "End" => "\x1b[F",
            "Delete" => "\x1b[3~",
            "PageUp" => "\x1b[5~",
            "PageDown" => "\x1b[6~",
            _ => character >= ' ' ? character.ToString() : ""
        };
    }
}
