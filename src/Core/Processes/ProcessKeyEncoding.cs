namespace Zenith.Core.Processes;

internal static class ProcessKeyEncoding
{
    // Cosmos layouts commonly provide no text for Ctrl+letter. Carry the logical
    // letter with its modifier so clients can implement chords. Preserve AltGr
    // text and ordinary Unicode; never infer text for non-letter special keys.
    public static int Text(char text, string key, int modifiers)
    {
        if ((modifiers & 6) == 2 && text < ' ' && key.Length == 1
            && key[0] is >= 'A' and <= 'Z') { return key[0] + ('a' - 'A'); }
        // Ctrl+digit likewise (kernel 0.0.58): Cosmos names those keys D0..D9.
        if ((modifiers & 6) == 2 && text < ' ' && key.Length == 2 && key[0] == 'D'
            && key[1] is >= '0' and <= '9') { return key[1]; }
        // Ctrl with the minus, equals and keypad plus/minus keys (kernel 0.0.65, zoom shortcuts). Only keys whose
        // character is the same on every layout: the names are US positions (RBracket is "+" on German keyboards).
        if ((modifiers & 6) == 2 && text < ' ')
        {
            switch (key) { case "Minus": case "NumMinus": return '-'; case "Equal": return '='; case "NumPlus": return '+'; }
        }
        return text;
    }
}
