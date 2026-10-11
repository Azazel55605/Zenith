using Cosmos.Kernel.System.Graphics;
using Zenith.Apps;
using Zenith.Core;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Core.Storage;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>The temporary floating desktop, isolated behind the session lifecycle.</summary>
internal sealed class DesktopSessionHost : ISessionHost
{
    private Desktop _desktop = null!;
    public void Start()
    {
        Fonts.Load();
        _desktop = new Desktop(Canvas.GetFullScreen());
        using var session = CredentialContext.Enter(Accounts.Session);
        _desktop.Windows.Open(new WelcomeWindow());
        _desktop.Windows.Open(new TerminalWindow());
        Log.Write("kernel", "desktop ready (" + (SystemMounts.Mode == BootMode.Live ? "live" : "installed") + ")");
    }
    public void Tick() => _desktop.Tick();
}
