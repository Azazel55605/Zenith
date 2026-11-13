using Zenith.Core.Security;

namespace Zenith.Core.Portal;

/// <summary>The portal interfaces this kernel offers, set up once at startup.</summary>
internal static class Portals
{
    public static AuthorizationPortal Authorization { get; private set; } = null!;
    public static void Initialize()
    {
        Authorization = new AuthorizationPortal(Accounts.IsAdministrator,
            () => Accounts.Service?.RootConfigured ?? false,
            uid => Accounts.Service?.Name(uid) ?? (uid == 0 ? "root" : uid.ToString()));
        PortalBroker.Register(Authorization);
    }
}
