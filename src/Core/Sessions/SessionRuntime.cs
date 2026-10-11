using System;
using System.Runtime.InteropServices;
using Cosmos.Kernel.Boot.Limine;

namespace Zenith.Core.Sessions;

internal interface ISessionHost
{
    void Start();
    void Tick();
}

internal static class SessionRuntime
{
    private static ISessionHost? s_host;
    public static bool Started { get; private set; }
    public static Func<bool> DesktopAvailable { get; set; } = () => true;
    public static SessionSelection Selected { get; private set; } = new(SessionMode.Desktop, "built-in default", null);
    public static bool Available(SessionMode mode) => (mode == SessionMode.Desktop && DesktopAvailable())
        || (mode == SessionMode.Headless && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64);
    public static unsafe void Start(SessionMode saved, Func<SessionMode, ISessionHost> factory)
    {
        string arguments = Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "";
        Selected = SessionModes.Select(saved, arguments, Available);
        if (Selected.Diagnostic is string diagnostic) { Log.Write("session", diagnostic); }
        Log.Write("session", SessionModes.Name(Selected.Mode) + " selected from " + Selected.Source);
        s_host = factory(Selected.Mode);
        s_host.Start();
        Started = true;
    }
    public static void Tick() => s_host?.Tick();
}
