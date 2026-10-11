using System;
using Zenith.Core.Input;
using Zenith.Core.Sessions;
using Zenith.Core.Time;

namespace Zenith.Core.Configuration;

internal static class SystemConfiguration
{
    public static SettingsService Settings { get; private set; } = null!;
    public static void Initialize()
    {
        Settings = new SettingsService(new NativeSettingsStore(),
            name => { if (!KeyboardLayouts.Apply(name)) { throw new InvalidOperationException("keyboard apply failed"); } },
            name => { if (!SystemClock.TrySetZone(name)) { throw new InvalidOperationException("clock apply failed"); } },
            SessionRuntime.Available);
        foreach (string error in Settings.Load()) { Log.Write("settings", error); }
        Log.Write("settings", "loaded keyboard=" + Settings.Current.Keyboard + " timezone=" + Settings.Current.TimeZone
            + " session=" + SessionModes.Name(Settings.Current.DefaultSession));
    }
}
