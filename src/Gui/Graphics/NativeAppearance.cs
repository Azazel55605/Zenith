using System;
using System.IO;
using System.Linq;
using System.Text;
using Zenith.Core;
using Zenith.Core.Configuration;
using Zenith.Core.Packages;
using Zenith.Core.Security;

namespace Zenith.Gui.Graphics;

internal static class NativeAppearance
{
    public static string? Resource(string path)
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        try
        {
            var store = new NativePackageStore();
            var file = new PackageManager(store).List().SelectMany(p => p.Manifest.Files).FirstOrDefault(f => f.Path == path);
            if (file is null || file.Length > 4096) { return null; }
            byte[] bytes = store.Read(path, 4096);
            if (bytes.Length != file.Length || PackagePlanner.Digest(bytes) != file.Digest || store.Mode(path) != file.Mode) { return null; }
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch { return null; }
    }
    private static ulong s_revision;
    private static uint s_uid = uint.MaxValue;
    private static bool s_personal;
    public static AppearanceSelection Current { get; private set; } = new(0,0,1);
    private static AppearancePalette? s_palette;
    public static void ApplyPending(bool force = false)
    {
        var service = SystemConfiguration.Appearance; var profile = SystemConfiguration.PersonalAppearance.ForDisplay(Accounts.Session.Uid);
        var target = profile.Saved; var palette = service.Palette;
        if (!force && s_uid == Accounts.Session.Uid && s_revision == target.Revision && ReferenceEquals(s_palette, palette)) { return; }
        if (service.UsePalette || !profile.Inherited || s_personal) { Theme.ApplyAppearance(palette, target.Mode, target.Accent); }
        s_personal = !profile.Inherited;
        s_uid = Accounts.Session.Uid; s_revision = target.Revision; s_palette = palette; Current = target;
        SystemConfiguration.PersonalAppearance.MarkActive(s_uid,target);
        service.MarkActive(profile.Inherited ? service.Saved : target with { Revision = 0 });
    }
    public static void Reload()
    {
        bool valid = Theme.Apply(Resource("/usr/share/zenith/appearance/default/theme.conf"));
        Log.Write("appearance", "theme=" + Theme.Name + (valid ? "" : "; invalid resource, recovery palette"));
        using var root = CredentialContext.Enter(Credentials.Root);
        Fonts.Reload();
        SystemConfiguration.Appearance.Resources(Resource("/usr/share/zenith/appearance/default/native.conf"));
        ApplyPending(true);
    }
}
