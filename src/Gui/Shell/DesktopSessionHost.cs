using Cosmos.Kernel.System.Graphics;
using System.Runtime.InteropServices;
using Zenith.Apps;
using Zenith.Core;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Core.Storage;
using Zenith.Gui.Graphics;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Gui.Shell;

/// <summary>The temporary floating desktop, isolated behind the session lifecycle.</summary>
internal sealed class DesktopSessionHost : ISessionHost
{
    [DllImport("*", EntryPoint = "zenith_has_display")]
    [SuppressGCTransition]
    private static extern int HasPciDisplay();
    public static unsafe bool HasDisplay()
    {
        var response = Cosmos.Kernel.Boot.Limine.Limine.Framebuffer.Response;
        return (response != null && response->FramebufferCount > 0) || HasPciDisplay() != 0;
    }
    private Desktop _desktop = null!;
    public bool CanStop => StopBlocker(null) is null;
    public string? StopBlocker(CommandShell? requester)
    {
        foreach (var window in _desktop.Windows.Windows)
        {
            if (window.IsClosed) { continue; }
            if (window is EditorWindow editor && editor.HasUnsavedChanges) { return "Save or close the editor before logging out."; }
            if (window is TerminalWindow terminal && terminal.IsBusy && (requester is null || !terminal.OwnsShell(requester))) { return "A terminal command is still running."; }
        }
        return null;
    }
    public void Stop()
    {
        foreach (var window in _desktop.Windows.Windows) { window.EndSession(); }
        TerminalWindow.OpenApp = null; FileManagerWindow.OpenApp = null;
        CommandShell.Register(new Zenith.Core.Shell.Command("fps", "fps", "Compositor statistics", c => c.Fail("desktop is not running")));
        _desktop = null!;
    }
    public void BeginStop()
    {
        foreach (var window in _desktop.Windows.Windows)
        { if (window is TerminalWindow terminal) { terminal.PrepareSessionEnd(); } }
    }
    public void Start()
    {
        NativeAppearance.Reload();
        _desktop = new Desktop(SessionDisplay.Screen);
        using var session = CredentialContext.Enter(Accounts.Session);
        if (SystemMounts.Mode == BootMode.Live || Accounts.Service is null || !Accounts.Service.HasPasswords)
        {
            _desktop.Windows.Open(new WelcomeWindow());
            _desktop.Windows.Open(new TerminalWindow());
        }
        Log.Write("kernel", "desktop ready (" + (SystemMounts.Mode == BootMode.Live ? "live" : "installed") + ")");
    }
    public void Tick() => _desktop.Tick();
}
