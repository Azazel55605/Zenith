using System;
using System.Runtime.InteropServices;
using Cosmos.Kernel.Boot.Limine;
using Zenith.Core.Security;
using Zenith.Core.Storage;
using Zenith.Core.Shell.Commands;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Core.Sessions;

internal interface ISessionHost
{
    void Start();
    void Tick();
    bool CanStop { get; }
    string? StopBlocker(CommandShell? requester);
    void Stop();
    void BeginStop();
}

internal static class SessionRuntime
{
    private static ISessionHost? s_host;
    public static bool Started { get; private set; }
    public static Func<bool> DesktopAvailable { get; set; } = () => true;
    public static Func<SessionMode, ISessionHost>? LoginFactory { get; set; }
    private static Func<SessionMode, ISessionHost> s_factory = null!;
    private static volatile bool s_logout;
    private static SessionMode? s_next;
    public static bool IsLogin { get; private set; }
    public static string? Message { get; private set; }
    public static SessionSelection Selected { get; private set; } = new(SessionMode.Desktop, "built-in default", null);
    public static bool Available(SessionMode mode) => (mode == SessionMode.Desktop && DesktopAvailable())
        || (mode == SessionMode.Headless && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64);
    public static unsafe void Start(SessionMode saved, Func<SessionMode, ISessionHost> factory)
    {
        string arguments = Marshal.PtrToStringUTF8((IntPtr)Limine.Cmdline) ?? "";
        Selected = SessionModes.Select(saved, arguments, Available);
        if (Selected.Diagnostic is string diagnostic) { Log.Write("session", diagnostic); }
        Log.Write("session", SessionModes.Name(Selected.Mode) + " selected from " + Selected.Source);
        s_factory = factory;
        StartSelected();
        Started = true;
    }
    private static void StartSelected()
    {
        IsLogin = DesktopAvailable() && LoginFactory is not null &&
            (Accounts.Error is not null || (Accounts.Service is not null && Accounts.Service.HasPasswords && Accounts.Session.Uid == 65534));
        s_host = IsLogin ? LoginFactory!(Selected.Mode) : s_factory(Selected.Mode);
        s_host.Start();
    }
    public static string? RequestLogout(CommandShell? requester = null, SessionMode? next = null)
    {
        lock (Installer.MaintenanceGate)
        {
            if (requester?.IsBackground == true) { return "Background jobs cannot change the session."; }
            if (IsLogin) { return "already at login"; }
            if (DesktopAvailable() && (Accounts.Service is null || !Accounts.Service.HasPasswords))
            { Message = "Configure installed accounts before logging out."; return Message; }
            if (next.HasValue && !Available(next.Value)) { return "session unavailable"; }
            string? blocker = MutationBlocker() ?? s_host?.StopBlocker(requester);
            if (blocker is not null) { Message = blocker; Log.Write("session", "logout refused: " + blocker); return blocker; }
            s_next = next ?? Selected.Mode;
            s_host?.BeginStop(); s_logout = true; Message = null; return null;
        }
    }
    public static string? ChangeBlocker(CommandShell requester) => MutationBlocker() ?? s_host?.StopBlocker(requester);
    private static string? MutationBlocker() => Installer.IsRunning || PackageCommands.IsRunning ||
        Zenith.Core.Boot.NativeKernelUpdates.IsRunning || Zenith.Core.Processes.NativeProcess.IsRunning ? "Wait for the active installation or update to finish." : null;
    public static void Activate(Credentials identity, SessionMode mode)
    {
        if (!IsLogin || !Available(mode)) { throw new InvalidOperationException("login session activation refused"); }
        s_host!.Stop(); Accounts.Login(identity);
        Selected = new(mode, "authenticated login", null); IsLogin = false;
        s_host = s_factory(mode); s_host.Start();
        Log.Write("session", "authenticated session started: " + SessionModes.Name(mode));
    }
    public static void Tick()
    {
        s_host?.Tick();
        if (!s_logout || s_host is null || !s_host.CanStop) { return; }
        lock (Installer.MaintenanceGate)
        {
            if (MutationBlocker() is not null) { return; }
            s_logout = false; s_host.Stop(); Accounts.Logout();
            if (s_next.HasValue) { Selected = new(s_next.Value, "logout selection", null); s_next = null; }
            Zenith.Gui.Clipboard.Text = string.Empty;
            StartSelected(); Log.Write("session", "session ended");
        }
    }
}
