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
        add(new Command("display", "display [test WIDTH HEIGHT SCALE|PERCENT | keep | revert]", "Primary display modes and guarded preview", Display));
        add(new Command("settings", "settings [show | set keyboard|timezone|session|animations|taskbar|windows|wallpaper|cursor|cursor-size|decoration|widgets|buttons|splash|login|boot|sounds|icons|sound-level|sound-off|translucency VALUE | set appearance dark|light ACCENT | set materials solid|transparent|blur|background|background-blur]", "Shared system settings", Settings));
        add(new Command("session", "session [status | list | set-default MODE | switch MODE]", "Select a session or return to login", Session));
        add(new Command("notify", "notify [--icon NAME] TITLE [BODY...] | notify history | notify dnd on|off | notify popup short|normal|long", "Post a notification; show history; Do Not Disturb", Notify));
    }
    private static int Notify(CommandContext c)
    {
        var settings = SystemConfiguration.Settings;
        if (c.Args.Length == 1 && c.Args[0] == "history")
        {
            foreach (var n in Zenith.Core.Notifications.NotificationCenter.History()) { c.WriteLine(n.Id + " " + n.Source + ": " + n.Title + (n.Body.Length > 0 ? " - " + n.Body : "")); }
            c.WriteLine("dnd=" + (settings.DoNotDisturb ? "on" : "off") + " popup=" + SettingsService.PopupNames[settings.Popup]);
            return 0;
        }
        if (c.Args.Length == 2 && c.Args[0] is "dnd" or "popup")
        {
            using var root = Zenith.Core.Security.CredentialContext.Enter(Zenith.Core.Security.Credentials.Root);
            bool dnd = settings.DoNotDisturb; uint popup = settings.Popup;
            if (c.Args[0] == "dnd") { if (c.Args[1] is not ("on" or "off")) { return c.Fail("notify dnd on|off"); } dnd = c.Args[1] == "on"; }
            else { int i = Array.IndexOf(SettingsService.PopupNames, c.Args[1]); if (i < 0) { return c.Fail("notify popup short|normal|long"); } popup = (uint)i; }
            var (_, revision) = settings.Snapshot();
            if (settings.SaveNotifications(dnd, popup, revision) != 0) { return c.Fail("could not save notification settings"); }
            c.WriteLine("saved " + c.Args[0] + " " + c.Args[1]); return 0;
        }
        int at = 0; string icon = "";
        if (c.Args.Length >= 2 && c.Args[0] == "--icon") { icon = c.Args[1]; at = 2; }
        if (c.Args.Length <= at) { return c.Fail("usage: notify [--icon NAME] TITLE [BODY...]"); }
        string body = c.Args.Length > at + 1 ? string.Join(" ", c.Args, at + 1, c.Args.Length - at - 1) : "";
        int id = Zenith.Core.Notifications.NotificationCenter.Post("Shell", c.Args[at], body, icon, 0);
        if (id < 0) { return c.Fail(id == -16 ? "too many notifications; wait a few seconds" : "title up to 64 bytes, body up to 160, icon a lower-case name"); }
        c.WriteLine("posted " + id); return 0;
    }
    private static int Display(CommandContext c)
    {
        var service=SystemConfiguration.Display;var state=service.Snapshot(DisplayService.Now);
        if(c.Args.Length==0)
        {
            c.WriteLine("DISPLAY "+state.Driver+" "+state.Current.Width+"x"+state.Current.Height+" scale="+state.Current.Scale+" phase="+state.Phase+" seconds="+state.Seconds+" error="+state.Error+" percent="+state.Current.Percent);
            for(int i=0;i<state.Modes.Length;i++)
            {
                var m=state.Modes[i];var percents=new System.Collections.Generic.List<string>();
                foreach(int p in DisplayLayout.Percents) { if((m with{Percent=p}).Usable && (p is 100 or 200 || service.Fractional)) { percents.Add(p.ToString()); } }
                c.WriteLine(i+": "+m.Width+"x"+m.Height+" percent="+string.Join(",",percents));
            }
            return 0;
        }
        long result;
        if(c.Args.Length==4 && c.Args[0]=="test" && int.TryParse(c.Args[1],out int w) && int.TryParse(c.Args[2],out int h) && uint.TryParse(c.Args[3],out uint scale))
        {
            uint index=uint.MaxValue;
            for(int i=0;i<state.Modes.Length;i++) { if(state.Modes[i].Width==w && state.Modes[i].Height==h) { index=(uint)i;break; } }
            result=service.Request((int)Zenith.Core.Security.CredentialContext.Current.Uid,0,1,index,scale,state.Revision,0);
        }
        else if(c.Args.Length==1 && (c.Args[0]=="keep" || c.Args[0]=="revert"))
        { result=service.Request((int)Zenith.Core.Security.CredentialContext.Current.Uid,0,c.Args[0]=="keep"?2U:3U,0,0,state.Revision,state.Token); }
        else { return c.Fail("usage: display [test WIDTH HEIGHT SCALE|PERCENT | keep | revert]"); }
        if(result!=0) { return c.Fail("display request refused ("+result+")"); }
        c.WriteLine("Display request queued; previews revert after 15 seconds unless kept.");return 0;
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
            c.WriteLine("animations=" + SettingsService.AnimationNames[settings.Current.Animation]);
            c.WriteLine("taskbar=" + SettingsService.TaskbarNames[settings.Current.Taskbar]);
            c.WriteLine("windows=" + SettingsService.WindowsNames[settings.Current.Windows] + " (new windows)");
            c.WriteLine("wallpaper=" + SettingsService.WallpaperNames[settings.Current.Wallpaper] + " cursor=" + SettingsService.CursorNames[settings.Current.Cursor]);
            foreach (var style in SettingsService.Styles) { c.WriteLine(style.Key.ToLowerInvariant().Replace('_', '-') + "=" + style.Names[settings.Style(style.Field)]); }
            {
                var off = new System.Collections.Generic.List<string>();
                for (int i = 0; i < SettingsService.SoundEvents.Length; i++) { if ((settings.SoundMask & (1u << i)) == 0) { off.Add(SettingsService.SoundEvents[i]); } }
                c.WriteLine("sound-level=" + settings.SoundLevel + "% sound-off=" + (off.Count == 0 ? "none" : string.Join(",", off)));
            }
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
        if (c.Args.Length != 3 || c.Args[0] != "set") { return c.Fail("usage: settings [show | set keyboard|timezone|session|animations|taskbar|windows|wallpaper|cursor|cursor-size|decoration|widgets|buttons|splash|login|boot|sounds|icons|sound-level|sound-off|translucency VALUE | set appearance dark|light ACCENT | set materials solid|transparent|blur|background|background-blur]"); }
        switch (c.Args[1])
        {
            case "keyboard": settings.SetKeyboard(c.Args[2]); break;
            case "timezone": settings.SetTimeZone(c.Args[2]); break;
            case "session": return SaveSession(c, c.Args[2]);
            case "animations": settings.SetAnimation(c.Args[2]); break;
            case "taskbar": settings.SetTaskbar(c.Args[2]); break;
            case "windows": settings.SetWindows(c.Args[2]); break;
            case "wallpaper": settings.SetWallpaper(c.Args[2]); break;
            case "cursor": settings.SetCursor(c.Args[2]); break;
            case "sound-level" or "sound-off":
            {
                // sound-level 0..100; sound-off a comma list of events (or "none").
                using var root = Zenith.Core.Security.CredentialContext.Enter(Zenith.Core.Security.Credentials.Root);
                uint level = settings.SoundLevel, mask = settings.SoundMask;
                if (c.Args[1] == "sound-level") { if (!uint.TryParse(c.Args[2], out level) || level > 100) { return c.Fail("sound-level takes 0..100"); } }
                else
                {
                    mask = 0xFFF;
                    foreach (string name in c.Args[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (name == "none") { continue; }
                        int i = Array.IndexOf(SettingsService.SoundEvents, name);
                        if (i < 0) { return c.Fail("unknown sound event " + name); }
                        mask &= ~(1u << i);
                    }
                }
                var (_, revision) = settings.Snapshot();
                if (settings.SaveSounds(level, mask, revision) != 0) { return c.Fail("could not save sounds"); }
                break;
            }
            default:
            {
                // Style choices (U14): cursor-size, decoration, widgets, buttons, splash, login, boot, sounds.
                SettingsService.StyleChoice? style = null;
                foreach (var candidate in SettingsService.Styles) { if (candidate.Key.ToLowerInvariant().Replace('_', '-') == c.Args[1]) { style = candidate; } }
                if (style is null) { return c.Fail("unknown setting"); }
                settings.SetStyle(style.Field, c.Args[2]); break;
            }
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
