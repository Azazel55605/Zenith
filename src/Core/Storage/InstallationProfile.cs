using System;
using Zenith.Core.Security;

namespace Zenith.Core.Storage;

/// <summary>Validated initial identity; stores password hashes, never plaintext secrets.</summary>
internal sealed class InstallationProfile
{
    public string Hostname { get; }
    public string User { get; }
    public string Passwd => "root:x:0:0:root:/root:/bin/sh\n" + User + ":x:1000:1000:" + User + ":/home/" + User + ":/bin/sh\n";
    public string Group => "root:x:0:\nusers:x:100:" + User + "\n" + User + ":x:1000:\n";
    public string Shadow { get; }
    private InstallationProfile(string hostname, string user, string rootHash, string userHash)
    {
        Hostname = hostname; User = user;
        Shadow = "root:" + rootHash + ":::::::\n" + user + ":" + userHash + ":::::::\n";
        _ = new AccountDatabase(Passwd, Group, Shadow);
    }
    public static void ValidateNames(string hostname, string user)
    {
        if (hostname.Length is < 1 or > 63 || hostname[0] == '-' || hostname[^1] == '-')
        { throw new ArgumentException("hostname must be a 1..63 character lowercase DNS label"); }
        foreach (char c in hostname)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            { throw new ArgumentException("hostname accepts lowercase letters, digits and hyphens"); }
        }
        if (!AccountDatabase.ValidName(user) || user is "root" or "nobody" or "users")
        { throw new ArgumentException("choose a lowercase user name other than root, nobody or users"); }
    }
    public static InstallationProfile Create(string hostname, string user, string rootPassword, string rootRepeat,
        string userPassword, string userRepeat, IEntropySource entropy)
    {
        ValidateNames(hostname, user);
        if (rootPassword != rootRepeat || userPassword != userRepeat) { throw new ArgumentException("passwords do not match"); }
        // Generate both records before the installer can erase the target.
        return new(hostname, user, PasswordHash.Create(rootPassword, entropy), PasswordHash.Create(userPassword, entropy));
    }
}
