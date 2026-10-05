using System;
using System.Collections.Generic;
using Zenith.Apps;

namespace Zenith.Gui.Shell;

/// <summary>An installable application: what the launcher shows and how to open it.</summary>
internal sealed class AppInfo
{
    public AppInfo(string name, string description, Func<Window> create)
    {
        Name = name;
        Description = description;
        Create = create;
    }

    public string Name { get; }
    public string Description { get; }
    public Func<Window> Create { get; }
}

/// <summary>The built-in applications. Register new apps here to make them appear in the launcher.</summary>
internal static class AppRegistry
{
    public static readonly List<AppInfo> Apps = new()
    {
        new AppInfo("Welcome", "Getting started", () => new WelcomeWindow()),
        new AppInfo("Terminal", "Command line shell", () => new TerminalWindow()),
        new AppInfo("Notes", "Jot something down", () => new NotesWindow()),
        new AppInfo("System", "About this machine", () => new SystemInfoWindow()),
    };

    public static bool Exists(string name)
        => Apps.Exists(app => string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase));
}
