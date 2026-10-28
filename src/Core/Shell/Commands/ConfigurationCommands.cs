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
        add(new Command("settings", "settings [show | set keyboard|timezone|session|translucency VALUE | set appearance dark|light ACCENT | set materials solid|transparent|blur|background|background-blur]", "Shared system settings", Settings));
        add(new Command("session", "session [status | list | set-default MODE | switch MODE]", "Select a session or return to login", Session));
    }
    private static int Settings(CommandContext c)
    {
        var settings = SystemConfiguration.Settings;
        if (c.Args.Length > 0 && c.Args[0] == "personal")
        {
            uint uid=Zenith.Core.Security.CredentialContext.Current.Uid;var personal=SystemConfiguration.PersonalAppearance;
            var state=personal.Snapshot(uid);
            if(c.Args.Length==1 || (c.Args.Length==2&&c.Args[1]=="show"))
            { c.WriteLine("scope: personal uid="+uid+"; "+(state.Inherited?"following system defaults":"personal override"));c.WriteLine("appearance="+AppearanceService.ModeName(state.Saved.Mode)+"/"+AppearanceService.AccentName(state.Saved.Accent)+" materials="+state.Saved.Materials+" translucency="+state.Saved.Translucency+"%");return 0; }
            uint mode=state.Saved.Mode,accent=state.Saved.Accent,materials=state.Saved.Materials,level=state.Saved.Translucency;
            bool inherit=c.Args.Length==2&&c.Args[1]=="inherit";
            if(!inherit)
            {
                if(c.Args.Length==4&&c.Args[1]=="appearance") { if(!AppearanceService.TryMode(c.Args[2],out mode)||!AppearanceService.TryAccent(c.Args[3],out accent))return c.Fail("invalid personal appearance"); }
                else if(c.Args.Length==3&&c.Args[1]=="translucency") { if(!uint.TryParse(c.Args[2],out level)||level>100)return c.Fail("translucency requires 0..100"); }
                else return c.Fail("usage: settings personal [show|inherit|appearance dark|light ACCENT|translucency 0..100]");
            }
            int result=personal.Save(uid,mode,accent,materials,level,state.Saved.Revision,inherit);if(result!=0)return c.Fail("personal save failed ("+result+")");
            c.WriteLine(inherit?"Following system defaults.":"Personal appearance saved.");return 0;
        }
        if (c.Args.Length == 0 || (c.Args.Length == 1 && c.Args[0] == "show"))
        {
            c.WriteLine("scope: system; storage: " + (settings.Persistent ? "installed" : "live (volatile)"));
            c.WriteLine("keyboard=" + settings.Current.Keyboard + " (active " + KeyboardLayouts.Current + ")");
            c.WriteLine("timezone=" + settings.Current.TimeZone + " (active " + SystemClock.Zone.Name + ")");
            c.WriteLine("session=" + SessionModes.Name(settings.Current.DefaultSession) + " (next boot)");
            var appearance = SystemConfiguration.Appearance.Snapshot();
            c.WriteLine("appearance=" + AppearanceService.ModeName(appearance.Saved.Mode) + "/" + AppearanceService.AccentName(appearance.Saved.Accent)
                + " (active " + AppearanceService.ModeName(appearance.Active.Mode) + "/" + AppearanceService.AccentName(appearance.Active.Accent)
                + (appearance.Saved.Revision != appearance.Active.Revision ? "; pending desktop/next login" : "") + ")");
            var materials = SystemConfiguration.Appearance.MaterialStatus;
            c.WriteLine("materials saved=" + appearance.Saved.Materials + " effective=" + materials.Effective
                + " translucency=" + appearance.Saved.Translucency + "%" + " available=" + materials.Available + " fallback=" + materials.Reason);
            return 0;
        }
        if (c.Args.Length == 3 && c.Args[0] == "set" && c.Args[1] == "translucency")
        {
            if (!uint.TryParse(c.Args[2], out uint level) || level > 100) { return c.Fail("translucency requires 0..100"); }
            var service = SystemConfiguration.Appearance; var saved = service.Saved;
            int result = service.SaveFull(saved.Mode, saved.Accent, saved.Materials, saved.Revision, level);
            if (result != 0) { return c.Fail("translucency save failed (" + result + ")"); }
            c.WriteLine("saved translucency=" + level + "%; text contrast may limit the effective transparency"); return 0;
        }
        if (c.Args.Length == 3 && c.Args[0] == "set" && c.Args[1] == "materials")
        {
            uint effects = c.Args[2] switch { "solid" => 0U, "transparent" => 1U, "blur" => 3U, "background" => 5U, "background-blur" => 7U, _ => 8U };
            if (effects > 7) { return c.Fail("materials requires solid|transparent|blur|background|background-blur"); }
            var service = SystemConfiguration.Appearance;var current = service.Saved;
            int result = service.SaveFull(current.Mode, current.Accent, effects, current.Revision);
            if (result != 0) { return c.Fail("material save failed (" + result + ")"); }
            c.WriteLine("saved materials=" + effects + "; desktop applies on its next tick");return 0;
        }
        if (c.Args.Length == 4 && c.Args[0] == "set" && c.Args[1] == "appearance")
        {
            if (!AppearanceService.TryMode(c.Args[2], out uint mode) || !AppearanceService.TryAccent(c.Args[3], out uint accent))
            { return c.Fail("appearance requires dark|light and blue|violet|rose|amber|green|teal"); }
            var service = SystemConfiguration.Appearance;
            int result = service.Save(mode, accent, service.Saved.Revision);
            if (result != 0) { return c.Fail("appearance save failed (" + result + "); settings were not applied"); }
            c.WriteLine("saved appearance; desktop applies on its next tick, headless on next login" + (service.Persistent ? "" : " (live: volatile)"));
            return 0;
        }
        if (c.Args.Length != 3 || c.Args[0] != "set") { return c.Fail("usage: settings [show | set keyboard|timezone|session|translucency VALUE | set appearance dark|light ACCENT | set materials solid|transparent|blur|background|background-blur]"); }
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
            c.WriteLine("login=" + SessionRuntime.IsLogin + "; " + (SessionRuntime.Message ?? "ready"));
            return 0;
        }
        if (c.Args.Length == 1 && c.Args[0] == "list")
        {
            foreach (SessionMode mode in new[] { SessionMode.Desktop, SessionMode.Headless, SessionMode.WindowManager })
            { c.WriteLine(SessionModes.Name(mode) + ": " + (SessionRuntime.Available(mode)
                ? mode == SessionMode.Desktop ? "available (test desktop)" : SessionRuntime.DesktopAvailable() ? "available (local text console; serial fallback)" : "available (serial COM1)"
                : "planned, unavailable")); }
            return 0;
        }
        if (c.Args.Length == 2 && c.Args[0] == "set-default") { return SaveSession(c, c.Args[1]); }
        if (c.Args.Length == 2 && c.Args[0] == "switch")
        {
            if (!SessionModes.TryParse(c.Args[1], out var mode)) { return c.Fail("unknown session mode"); }
            string? error = SessionRuntime.RequestLogout(c.Shell, mode);
            if (error is not null) { return c.Fail(error); }
            c.WriteLine("Ending session; sign in to " + c.Args[1] + "."); return 0;
        }
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
