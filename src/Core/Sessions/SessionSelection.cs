using System;

namespace Zenith.Core.Sessions;

internal enum SessionMode { Desktop, Headless, WindowManager }
internal sealed record SessionSelection(SessionMode Mode, string Source, string? Diagnostic);

internal static class SessionModes
{
    public static string Name(SessionMode mode) => mode switch
    { SessionMode.Desktop => "de", SessionMode.Headless => "headless", SessionMode.WindowManager => "wm", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
    public static bool TryParse(string name, out SessionMode mode)
    {
        mode = name switch { "de" => SessionMode.Desktop, "headless" => SessionMode.Headless, "wm" => SessionMode.WindowManager, _ => (SessionMode)(-1) };
        return (int)mode >= 0;
    }
    // Availability comes from the registered hosts, not from the configuration schema.
    public static SessionSelection Select(SessionMode saved, string commandLine, Func<SessionMode, bool> available)
    {
        string? diagnostic = null;
        SessionMode requested = saved;
        string source = "saved default";
        bool found = false;
        if (commandLine.Length > 4096) { diagnostic = "boot command line exceeds session parser limit"; }
        else
        {
            foreach (string token in commandLine.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith("session=", StringComparison.Ordinal)) { continue; }
                if (found || !TryParse(token.Substring(8), out requested))
                { diagnostic = "invalid or duplicate session= argument; using saved default"; requested = saved; source = "saved default"; break; }
                found = true; source = "boot argument";
            }
        }
        if (!available(requested))
        {
            SessionMode fallback = available(SessionMode.Desktop) ? SessionMode.Desktop : SessionMode.Headless;
            diagnostic = "session '" + Name(requested) + "' is unavailable on this boot; using " + Name(fallback);
            requested = fallback; source = "capability fallback";
        }
        if (!available(requested)) { throw new InvalidOperationException("no usable session host"); }
        return new(requested, source, diagnostic);
    }
}
