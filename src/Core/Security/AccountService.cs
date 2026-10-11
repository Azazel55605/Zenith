using System;

namespace Zenith.Core.Security;

internal interface IAccountStore
{
    string Read(string name);
    void Replace(string name, string text);
    void CreateHome(string name, uint uid, uint gid);
}

/// <summary>Serialized account changes; passwd is published after its group and locked shadow entry.</summary>
internal sealed class AccountService
{
    private readonly IAccountStore _store;
    private readonly IEntropySource _entropy;
    private readonly object _gate = new();
    public AccountService(IAccountStore store, IEntropySource entropy) { _store = store; _entropy = entropy; }
    private AccountDatabase Load() => new(_store.Read("passwd"), _store.Read("group"), _store.Read("shadow"));
    public bool RootConfigured { get { lock (_gate) { return Load().Find("root")!.Password != "!"; } } }
    public UserAccount? Find(string name) { lock (_gate) { return Load().Find(name); } }
    public string Name(uint uid)
    {
        lock (_gate) { foreach (var user in Load().Users) { if (user.Uid == uid) { return user.Name; } } }
        return uid == 65534 ? "nobody" : uid.ToString();
    }
    public Credentials? Authenticate(string name, string password)
    {
        lock (_gate)
        {
            var db = Load(); var user = db.Find(name);
            return user is not null && PasswordHash.Verify(password, user.Password) ? db.Identity(user) : null;
        }
    }
    public Credentials RootSwitch(string name)
    {
        if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("operation requires root"); }
        lock (_gate) { var db = Load(); return db.Identity(db.Find(name) ?? throw new ArgumentException("unknown user")); }
    }
    public void SetPassword(string name, string? oldPassword, string password)
    {
        lock (_gate)
        {
            var user = Load().Find(name) ?? throw new ArgumentException("unknown user");
            var caller = CredentialContext.Current;
            if (!caller.IsRoot && (caller.Uid != user.Uid || oldPassword is null || !PasswordHash.Verify(oldPassword, user.Password)))
            { throw new UnauthorizedAccessException("authentication failed"); }
            string hash = PasswordHash.Create(password, _entropy);
            string shadow = _store.Read("shadow");
            string[] lines = shadow.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(name + ":", StringComparison.Ordinal))
                { var f = lines[i].Split(':'); f[1] = hash; lines[i] = string.Join(":", f); }
            }
            _store.Replace("shadow", string.Join("\n", lines));
            Load();
        }
    }
    public uint Add(string name)
    {
        if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("operation requires root"); }
        if (!AccountDatabase.ValidName(name) || name == "root" || name == "nobody") { throw new ArgumentException("invalid user name"); }
        lock (_gate)
        {
            var db = Load();
            if (db.Find(name) is not null) { throw new ArgumentException("user already exists"); }
            uint id = 1000;
            while (db.HasUid(id) || db.HasGroup(id)) { if (++id >= 65534) { throw new InvalidOperationException("no available user IDs"); } }
            // Validate all records before the first write. Extras are harmless until passwd commits.
            string passwd = _store.Read("passwd").TrimEnd('\n') + "\n" + name + ":x:" + id + ":" + id + ":" + name + ":/home/" + name + ":/bin/sh\n";
            string group = _store.Read("group").TrimEnd('\n') + "\n" + name + ":x:" + id + ":\n";
            string shadow = _store.Read("shadow").TrimEnd('\n') + "\n" + name + ":!:::::::\n";
            _ = new AccountDatabase(passwd, group, shadow);
            _store.Replace("group", group);
            _store.Replace("shadow", shadow);
            _store.CreateHome(name, id, id);
            _store.Replace("passwd", passwd);
            return id;
        }
    }
}
