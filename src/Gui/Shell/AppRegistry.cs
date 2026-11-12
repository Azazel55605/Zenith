using System;
using System.Collections.Generic;
using Zenith.Apps;

namespace Zenith.Gui.Shell;

/// <summary>An installable application: what the launcher shows and how to open it.</summary>
internal sealed class AppInfo
{
    public AppInfo(string name, string description, Func<string?, Window> create, string? native = null, string? action = null,
        bool builtIn = true, string? icon = null)
    {
        Name = name;
        Description = description;
        Create = create; Native = native; Action = action; BuiltIn = builtIn; Icon = icon;
    }

    /// <summary>Opens without its package (a built-in window stands in); otherwise the app is
    /// listed only while its native program is installed.</summary>
    public bool BuiltIn { get; }
    public string? Icon { get; }
    public bool Installed => Native is string native
        && Zenith.Core.Packages.NativePackageSource.State("/usr/share/zenith/native/" + native + ".elf") == Zenith.Core.Packages.PackagePathState.Occupied;

    public string? Native { get; }
    /// <summary>The name to show: an installed native app goes by its own name.</summary>
    public string DisplayName => Name == "Terminal" && Native is string native
        && Zenith.Core.Packages.NativePackageSource.State("/usr/share/zenith/native/" + native + ".elf") == Zenith.Core.Packages.PackagePathState.Occupied
            ? "Polaris" : Name;
    public string? Action { get; }
    public string Name { get; }
    public string Description { get; }
    /// <summary>Creates the app's window; the argument (e.g. a file path) may be null.</summary>
    public Func<string?, Window> Create { get; }
}

/// <summary>The built-in applications. Register new apps here to make them appear in the launcher.</summary>
internal static class AppRegistry
{
    public static readonly List<AppInfo> Apps = new()
    {
        new AppInfo("Welcome", "Getting started", _ => new WelcomeWindow(), "desktop-tools", "welcome"),
        new AppInfo("Terminal", "Command line shell", _ => new TerminalWindow(), "polaris"),
        new AppInfo("Files", "Browse and manage files", path => new FileManagerWindow(path)),
        new AppInfo("Editor", "Edit text files", path => new EditorWindow(path)),
        new AppInfo("Sunrise", "Install Zenith", _ => new InstallerWindow()),
        new AppInfo("System", "About this machine", _ => new SystemInfoWindow(), "desktop-tools", "system"),
        new AppInfo("Meridian", "System settings", _ => new AppLaunchNotice("Install the zenith-settings package to open Meridian."), "settings", builtIn: false),
        new AppInfo("Observatory", "CPU, memory and running programs", _ => new AppLaunchNotice("Install the zenith-observatory package to open Observatory."), "observatory", builtIn: false),
        new AppInfo("Equinox", "Disks and partitions", _ => new AppLaunchNotice("Install the zenith-equinox package to open Equinox."), "equinox", builtIn: false),
    };

    public const string DescriptorDirectory = "/usr/share/zenith/apps";

    /// <summary>What the launcher lists now: built-in apps, package apps whose program is
    /// installed, and apps packages declare in <see cref="DescriptorDirectory"/>. Sunrise only in
    /// the live session. Alphabetical by display name.</summary>
    public static List<AppInfo> Visible()
    {
        var result = new List<AppInfo>();
        bool live = Zenith.Core.Storage.SystemMounts.Mode == Zenith.Core.Storage.BootMode.Live;
        foreach (var app in Apps)
        {
            if (app.Name == "Sunrise" && !live) { continue; }
            if (app.BuiltIn || app.Installed) { result.Add(app); }
        }
        foreach (var app in Declared()) { if (app.Installed && Find(result, app.Name) is null) { result.Add(app); } }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>An app by name: built-in or declared by a package.</summary>
    public static AppInfo? Find(string name) => Find(Apps, name) ?? Find(Declared(), name);
    private static AppInfo? Find(List<AppInfo> apps, string name)
    { foreach (var app in apps) { if (string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase)) { return app; } } return null; }

    /// <summary>Apps packages declare: NAME.app files of NAME=, DESCRIPTION=, ICON=, PROGRAM= (a
    /// native program in /usr/share/zenith/native) and optional ACTION= lines. Bounded: 32 files of
    /// at most 1 KiB; malformed ones are skipped.</summary>
    public static List<AppInfo> Declared()
    {
        var result = new List<AppInfo>();
        try
        {
            if (!System.IO.Directory.Exists(DescriptorDirectory)) { return result; }
            int seen = 0;
            foreach (string path in System.IO.Directory.GetFiles(DescriptorDirectory))
            {
                if (!path.EndsWith(".app", StringComparison.Ordinal) || ++seen > 32) { continue; }
                string? text = Zenith.Core.Storage.BoundedFile.ReadText(path, 1024);
                if (text is not null && Parse(text) is AppInfo app && Find(Apps, app.Name) is null) { result.Add(app); }
            }
        }
        catch (Exception e) { Zenith.Core.Log.Write("launcher", "app declarations unreadable: " + e.Message); }
        return result;
    }

    public static AppInfo? Parse(string text)
    {
        string? name = null, description = "", icon = null, program = null, action = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim(); int eq = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || eq < 1) { continue; }
            string key = line[..eq], value = line[(eq + 1)..].Trim();
            if (value.Length > 64) { return null; }
            foreach (char c in value) { if (c < ' ' || c > '~') { return null; } }
            switch (key)
            {
                case "NAME": name = value; break;
                case "DESCRIPTION": description = value; break;
                case "ICON": icon = value; break;
                case "PROGRAM": program = value; break;
                case "ACTION": action = value; break;
            }
        }
        if (name is null || name.Length == 0 || program is null || program.Length == 0) { return null; }
        foreach (char c in program) { if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { return null; } }
        string shown = name;
        return new AppInfo(name, description, _ => new AppLaunchNotice("Install the package that provides " + shown + "."), program, action, builtIn: false, icon);
    }

    /// <summary>The icon of a registered app; window titles may add to the app's name.</summary>
    public static string IconFor(string title)
    {
        if (title.Contains("Terminal") || title.Contains("Polaris")) { return "terminal"; }
        if (title.Contains("Files")) { return "folder"; }
        if (title.Contains("Editor")) { return "file-text"; }
        if (title.Contains("Welcome")) { return "hand"; }
        if (title.Contains("Sunrise")) { return "sunrise"; }
        if (title.Contains("System") || title.Contains("About")) { return "monitor"; }
        if (title.Contains("Meridian") || title.Contains("Settings")) { return "sliders-horizontal"; }
        if (title.Contains("Observatory")) { return "activity"; }
        if (title.Contains("Equinox")) { return "hard-drive"; }
        return "app-window";
    }

    public static bool Exists(string name)
        => string.Equals(name, "recovery-terminal", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Polaris", StringComparison.OrdinalIgnoreCase) || Find(name) is not null;
}
