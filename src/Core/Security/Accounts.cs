using System;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;
using Zenith.Core.Storage;

namespace Zenith.Core.Security;

/// <summary>Native account storage. Root access is confined to database operations.</summary>
internal static class Accounts
{
    public static AccountService? Service { get; private set; }
    public static Credentials Session { get; private set; } = Credentials.Root;
    public static int Generation { get; private set; }
    public static string? Error { get; private set; }
    public static void Initialize()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        if (!new VfsFileMetadata().TryRead("/", out var metadata) || !metadata.UnixAttributes) { return; }
        try
        {
            // Protected files also need protected ancestors: directory write
            // permission would otherwise permit replacing root-owned records/logs.
            foreach (string path in new[] { "/", "/etc", "/home", "/var", "/var/log" })
            { Secure(path, 0, 0, 0x1ed); }
            var store = new Store();
            // Upgrade only the known historical default lacking user's primary group.
            string group = store.Read("group");
            if (group == "root:x:0:\nusers:x:100:user\n") { store.Replace("group", group + "user:x:1000:\n"); }
            if (!File.Exists("/etc/shadow")) { store.Replace("shadow", "root:!:::::::\nuser:!:::::::\n"); }
            Secure("/etc/shadow", 0, 0, 0x180);
            Secure("/etc/passwd", 0, 0, 0x1a4);
            Secure("/etc/group", 0, 0, 0x1a4);
            Secure("/root", 0, 0, 0x1c0);
            var database = new AccountDatabase(store.Read("passwd"), store.Read("group"), store.Read("shadow"));
            foreach (var user in database.Users)
            { if (user.Uid == 1000) { Secure(user.Home, user.Uid, user.Gid, 0x1c0); } }
            Service = new AccountService(store, HardwareEntropy.Source);
            Session = Service.RootConfigured ? Credentials.Nobody : Credentials.Root;
        }
        catch (Exception e) { Error = e.Message; Service = null; Session = Credentials.Nobody; }
    }
    public static string Home(Credentials identity)
        => Service is null ? "/home/user" : Service.Find(Service.Name(identity.Uid))?.Home ?? "/";
    public static void Login(Credentials identity) { Session = identity; Generation++; }
    public static void Logout() { Session = Credentials.Nobody; Generation++; }
    private static void Secure(string path, uint uid, uint gid, ushort mode)
    {
        var metadata = new VfsFileMetadata();
        if (!metadata.SetOwner(path, uid, gid) || !metadata.SetMode(path, mode)) { throw new IOException("cannot secure " + path); }
    }
    private sealed class Store : IAccountStore
    {
        public string Read(string name)
        {
            using var root = CredentialContext.Enter(Credentials.Root);
            string path = "/etc/" + name;
            if (new FileInfo(path).Length > 65536) { throw new IOException("account database too large"); }
            return File.ReadAllText(path);
        }
        public void Replace(string name, string text)
        {
            using var root = CredentialContext.Enter(Credentials.Root);
            string path = "/etc/" + name;
            string temporary = path + ".new";
            if (File.Exists(temporary)) { File.Delete(temporary); }
            if (!VfsManager.TryCreateFile(temporary, (VfsMode)0x180) || !VfsManager.TryOpenFile(temporary, out var file)) { throw new IOException("cannot stage account database"); }
            using (file)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                if (file.Write(bytes) != bytes.Length || !file.TryFlush()) { throw new IOException("account database write failed"); }
                bytes.AsSpan().Clear();
            }
            if (!VfsManager.TryRename(temporary, path)) { throw new IOException("account database replacement failed"); }
            Secure(path, 0, 0, name == "shadow" ? (ushort)0x180 : (ushort)0x1a4);
            SystemMounts.SyncAll();
        }
        public void CreateHome(string name, uint uid, uint gid)
        {
            using var root = CredentialContext.Enter(Credentials.Root);
            string path = "/home/" + name;
            if (Directory.Exists(path)) { throw new IOException("home already exists"); }
            Directory.CreateDirectory(path);
            Secure(path, uid, gid, 0x1c0);
        }
    }
}
