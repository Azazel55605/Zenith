using System;
using Zenith.Core.Configuration;
using Zenith.Core.Input;
using Zenith.Core.Sessions;
using Zenith.Core.Time;

namespace Zenith.Core.Shell.Commands;

internal static class ConfigurationCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("settings", "settings [show | set keyboard|timezone|session VALUE]", "Shared system settings", Settings));
        add(new Command("session", "session [status | list | set-default MODE]", "Show or select the next boot's session", Session));
    }
    private static int Settings(CommandContext c)
    {
        var settings = SystemConfiguration.Settings;
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "show"))
        {
            c.WriteLine("scope: system; storage: " + (settings.Persistent ? "installed" : "live (volatile)"));
            c.WriteLine("keyboard=" + settings.Current.Keyboard + " (active " + KeyboardLayouts.Current + ")");
            c.WriteLine("timezone=" + settings.Current.TimeZone + " (active " + SystemClock.Zone.Name + ")");
            c.WriteLine("session=" + SessionModes.Name(settings.Current.DefaultSession) + " (next boot)");
            return 0;
        }
        if (c.Args.Length != 3 || c.Args[0] != "set") { return c.Fail("usage: settings [show | set keyboard|timezone|session VALUE]"); }
        switch (c.Args[1])
        {
            case "keyboard": settings.SetKeyboard(c.Args[2]); break;
            case "timezone": settings.SetTimeZone(c.Args[2]); break;
            case "session": return SaveSession(c, c.Args[2]);
            default: return c.Fail("unknown setting");
        }
        c.WriteLine("saved " + c.Args[1] + (settings.Persistent ? "" : " (live: lost at reboot)"));
        return 0;
    }
    private static int Session(CommandContext c)
    {
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "status"))
        {
            c.WriteLine("active=" + SessionModes.Name(SessionRuntime.Selected.Mode) + " (" + SessionRuntime.Selected.Source + ")");
            c.WriteLine("default=" + SessionModes.Name(SystemConfiguration.Settings.Current.DefaultSession));
            return 0;
        }
        if (c.Args.Length == 1 && c.Args[0] == "list")
        {
            foreach (SessionMode mode in new[] { SessionMode.Desktop, SessionMode.Headless, SessionMode.WindowManager })
            { c.WriteLine(SessionModes.Name(mode) + ": " + (SessionRuntime.Available(mode) ? "available (test desktop)" : "planned, unavailable")); }
            return 0;
        }
        if (c.Args.Length == 2 && c.Args[0] == "set-default") { return SaveSession(c, c.Args[1]); }
        return c.Fail("usage: session [status | list | set-default MODE]");
    }
    private static int SaveSession(CommandContext c, string name)
    {
        if (!SessionModes.TryParse(name, out var mode)) { return c.Fail("unknown session mode"); }
        SystemConfiguration.Settings.SetSession(mode);
        c.WriteLine("saved session=" + name + " for next boot" + (SystemConfiguration.Settings.Persistent ? "" : " (live: lost at reboot)"));
        return 0;
    }
}
