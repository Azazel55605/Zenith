using System;
using System.Collections.Generic;
using System.Text;

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
    public bool HasPasswords
    {
        get
        {
            lock (_gate)
            {
                foreach (var user in Load().Users) { if (user.Password != "!" && user.Password != "*") { return true; } }
                return false;
            }
        }
    }
    public string PublicSummary()
    {
        lock(_gate)
        {
            var result=new StringBuilder(); var caller=CredentialContext.Current; int shown=0;
            foreach(var user in Load().Users)
            {
                if(!caller.IsRoot && user.Uid!=caller.Uid) { continue; }
                if(shown++==24) { result.Append("More accounts are available through the CLI.\n");break; }
                result.Append(user.Name).Append("  uid=").Append(user.Uid).Append(" gid=").Append(user.Gid).Append("\nHome: ").Append(user.Home).Append("\n\n");
            }
            return result.Length==0 ? "No account record for this identity.\n" : result.ToString();
        }
    }
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
    /// <summary>The administrators' group: its members may use sudo and approve desktop portal
    /// privilege requests with their own password.</summary>
    public const string AdministratorGroup = "wheel";
    public const uint AdministratorGid = 10;
    /// <summary>Whether the account may administer the system, read now from the database (not
    /// from credentials taken at sign-in), so a membership change applies at once.</summary>
    public bool IsAdministrator(uint uid)
    {
        if (uid == 0) { return true; }
        lock (_gate)
        {
            var db = Load();
            foreach (var user in db.Users)
            {
                if (user.Uid != uid) { continue; }
                return user.Gid == AdministratorGid || Array.IndexOf(db.Members(AdministratorGid), user.Name) >= 0;
            }
            return false;
        }
    }
    /// <summary>Names of the groups the account belongs to: its primary group first.</summary>
    public string[] Groups(string name)
    {
        lock (_gate) { var db = Load(); var user = db.Find(name) ?? throw new ArgumentException("unknown user"); return db.GroupNames(user); }
    }
    /// <summary>Creates the administrators' group when it is missing. The first time, the account the
    /// installer made (uid 1000) joins it, as a fresh installation does. Root only.</summary>
    public bool EnsureAdministrators()
    {
        if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("operation requires root"); }
        lock (_gate)
        {
            var db = Load();
            if (db.GroupId(AdministratorGroup) is not null || db.HasGroup(AdministratorGid)) { return false; }
            string first = "";
            foreach (var user in db.Users) { if (user.Uid == 1000) { first = user.Name; } }
            string group = _store.Read("group").TrimEnd('\n') + "\n" + AdministratorGroup + ":x:" + AdministratorGid + ":" + first + "\n";
            _ = new AccountDatabase(_store.Read("passwd"), group, _store.Read("shadow"));
            _store.Replace("group", group);
            return true;
        }
    }
    /// <summary>Adds an account to a group or removes it (gpasswd -a / -d). Root only. A primary group
    /// cannot be left, and root stays an administrator whatever the group says.</summary>
    public bool SetMember(string groupName, string name, bool member)
    {
        if (!CredentialContext.Current.IsRoot) { throw new UnauthorizedAccessException("operation requires root"); }
        lock (_gate)
        {
            var db = Load();
            var user = db.Find(name) ?? throw new ArgumentException("unknown user");
            uint gid = db.GroupId(groupName) ?? throw new ArgumentException("unknown group");
            if (!member && user.Gid == gid) { throw new ArgumentException(groupName + " is the primary group of " + name); }
            var members = new List<string>(db.Members(gid));
            if (members.Contains(name) == member) { return false; }
            if (member) { members.Add(name); } else { members.Remove(name); }
            string[] lines = _store.Read("group").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string[] f = lines[i].Split(':');
                if (f.Length == 4 && f[0] == groupName) { f[3] = string.Join(",", members); lines[i] = string.Join(":", f); }
            }
            string group = string.Join("\n", lines);
            _ = new AccountDatabase(_store.Read("passwd"), group, _store.Read("shadow"));
            _store.Replace("group", group);
            return true;
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
