using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Core.Security;

internal sealed class UserAccount
{
    public UserAccount(string name, uint uid, uint gid, string home, string password)
    { Name = name; Uid = uid; Gid = gid; Home = home; Password = password; }
    public string Name { get; }
    public uint Uid { get; }
    public uint Gid { get; }
    public string Home { get; }
    internal string Password { get; }
}

/// <summary>Strict bounded parser: malformed/duplicate records fail closed, not partial login.</summary>
internal sealed class AccountDatabase
{
    private readonly Dictionary<string, UserAccount> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string[]> _groups = new();
    public IReadOnlyCollection<UserAccount> Users => _users.Values;

    public AccountDatabase(string passwd, string group, string shadow)
    {
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Lines(shadow))
        {
            string[] f = line.Split(':');
            if (f.Length != 9 || !ValidName(f[0]) || f[1].Length == 0 || f[1].Length > 160
                || (f[1] != "!" && !f[1].StartsWith("$zenith-pbkdf2-sha256$", StringComparison.Ordinal))
                || !passwords.TryAdd(f[0], f[1])) { throw new FormatException("invalid shadow database"); }
        }
        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Lines(group))
        {
            string[] f = line.Split(':');
            if (f.Length != 4 || !ValidName(f[0]) || f[1] != "x" || !Id(f[2], out uint gid)
                || !groupNames.Add(f[0]) || _groups.ContainsKey(gid)) { throw new FormatException("invalid group database"); }
            string[] members = f[3].Length == 0 ? Array.Empty<string>() : f[3].Split(',');
            if (members.Length > 128) { throw new FormatException("too many group members"); }
            foreach (string member in members) { if (!ValidName(member)) { throw new FormatException("invalid group member"); } }
            _groups.Add(gid, members);
        }
        var ids = new HashSet<uint>();
        foreach (string line in Lines(passwd))
        {
            string[] f = line.Split(':');
            if (f.Length != 7 || !ValidName(f[0]) || f[0] == "nobody" || f[1] != "x" || !Id(f[2], out uint uid) || !Id(f[3], out uint gid)
                || (uid == 0) != (f[0] == "root") || !_groups.ContainsKey(gid) || !ids.Add(uid)
                || !passwords.TryGetValue(f[0], out var password) || f[6] != "/bin/sh"
                || f[5] != (uid == 0 ? "/root" : "/home/" + f[0])
                || !_users.TryAdd(f[0], new UserAccount(f[0], uid, gid, f[5], password)))
            { throw new FormatException("invalid passwd database"); }
        }
        if (!_users.ContainsKey("root")) { throw new FormatException("root account missing"); }
    }

    private static IEnumerable<string> Lines(string text)
    {
        if (text.Length > 65536 || text.IndexOf('\0') >= 0 || text.IndexOf('\r') >= 0) { throw new FormatException("invalid account database size/encoding"); }
        string[] lines = text.Split('\n'); int count = 0;
        foreach (string line in lines)
        {
            if (line.Length == 0) { continue; }
            if (++count > 128 || line.Length > 4096) { throw new FormatException("account database limit exceeded"); }
            yield return line;
        }
    }

    public UserAccount? Find(string name) => _users.TryGetValue(name, out var user) ? user : null;
    public Credentials Identity(UserAccount account)
    {
        var groups = new List<uint>();
        foreach (var pair in _groups)
        {
            foreach (string member in pair.Value) { if (member == account.Name) { groups.Add(pair.Key); break; } }
        }
        return new Credentials(account.Uid, account.Gid, groups.ToArray());
    }
    public bool HasGroup(uint gid) => _groups.ContainsKey(gid);
    public bool HasUid(uint uid)
    {
        foreach (var user in _users.Values) { if (user.Uid == uid) { return true; } }
        return false;
    }
    public static bool ValidName(string name)
    {
        if (name.Length == 0 || name.Length > 32 || name[0] < 'a' || name[0] > 'z') { return false; }
        foreach (char c in name) { if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') { return false; } }
        return true;
    }
    private static bool Id(string text, out uint id)
        => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 65534 && text == id.ToString(CultureInfo.InvariantCulture);
}
