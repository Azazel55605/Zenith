using System.Collections.Generic;
using Zenith.Core.Portal;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Configuration;

/// <summary>"Always use this app" choices of the Open with portal: /etc/zenith/preferences/UID.open-with, in the
/// same root-owned, non-user-writable directory as personal appearance. Live sessions keep them in memory.</summary>
internal sealed class NativeOpenWithStore : IOpenWithStore
{
    private readonly Dictionary<uint, string> _live = new();
    private static bool Persistent => SystemMounts.Mode == BootMode.Installed;
    private static string PathFor(uint uid) => "/etc/zenith/preferences/" + uid + ".open-with";
    public string? Read(uint uid)
    {
        if (!Persistent) { return _live.GetValueOrDefault(uid); }
        using var root = CredentialContext.Enter(Credentials.Root);
        NativePersonalAppearanceStore.Ancestors(false); string path = PathFor(uid); NativePersonalAppearanceStore.Leaf(path);
        return new NativeSettingsStore().Read(path);
    }
    public void Replace(uint uid, string text)
    {
        if (!Persistent) { _live[uid] = text; return; }
        using var root = CredentialContext.Enter(Credentials.Root);
        NativePersonalAppearanceStore.Ancestors(true); string path = PathFor(uid);
        NativePersonalAppearanceStore.Leaf(path); NativePersonalAppearanceStore.Leaf(path + ".new");
        new NativeSettingsStore().Replace(path, text);
    }
}
