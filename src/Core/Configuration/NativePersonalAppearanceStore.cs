using System;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Security;
using Zenith.Core.Storage;

namespace Zenith.Core.Configuration;

// Root-owned, non-user-writable ancestry. No user-supplied path, home or UID text.
internal sealed class NativePersonalAppearanceStore : IPersonalAppearanceStore
{
    private readonly Dictionary<uint, string> _live = new();
    public bool Persistent => SystemMounts.Mode == BootMode.Installed;
    public bool Available(uint uid) => uid == 0 || (uid != 65534 && Accounts.Service?.Find(Accounts.Service.Name(uid))?.Uid == uid);
    private static string PathFor(uint uid) => "/etc/zenith/preferences/" + uid + ".conf";
    private static void Ancestors(bool create)
    {
        var metadata = new VfsFileMetadata();
        foreach (string path in new[] { "/", "/etc", "/etc/zenith", "/etc/zenith/preferences" })
        {
            if (!metadata.TryRead(path, out var stat))
            {
                if (!create) { return; }
                Directory.CreateDirectory(path);
                if (!metadata.SetOwner(path, 0, 0) || !metadata.SetMode(path, path.EndsWith("preferences") ? (ushort)0x1c0 : (ushort)0x1ed)
                    || !metadata.TryRead(path, out stat)) { throw new IOException("cannot protect preference directory"); }
            }
            if (stat.Type != "directory" || !stat.UnixAttributes || stat.Uid != 0 || (stat.Mode & 0x12) != 0 || (path.EndsWith("preferences") && (stat.Mode & 0x3f) != 0)) { throw new IOException("unsafe preference ancestor"); }
        }
    }
    private static void Leaf(string path)
    {
        if (new VfsFileMetadata().TryRead(path, out var stat) && (stat.Type != "regular file" || stat.Links != 1 || stat.Uid != 0 || (stat.Mode & 0x12) != 0))
        { throw new IOException("unsafe preference record"); }
    }
    public string? Read(uint uid)
    {
        if (!Persistent) { return _live.GetValueOrDefault(uid); }
        using var root = CredentialContext.Enter(Credentials.Root); Ancestors(false); string path = PathFor(uid); Leaf(path);
        return new NativeSettingsStore().Read(path);
    }
    public void Replace(uint uid, string text)
    {
        if (!Persistent) { _live[uid] = text; return; }
        using var root = CredentialContext.Enter(Credentials.Root); Ancestors(true); string path = PathFor(uid); Leaf(path); Leaf(path + ".new");
        new NativeSettingsStore().Replace(path, text);
    }
}
