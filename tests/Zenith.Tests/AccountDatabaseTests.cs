using System;
using Zenith.Core.Security;

namespace Zenith.Tests;

public class AccountDatabaseTests
{
    private const string Passwd = "root:x:0:0:root:/root:/bin/sh\nalice:x:1000:1000:Alice:/home/alice:/bin/sh\n";
    private const string Group = "root:x:0:\nalice:x:1000:\nstaff:x:20:alice\n";
    private const string Shadow = "root:!:0:0:99999:7:::\nalice:!:0:0:99999:7:::\n";
    [Fact]
    public void LockedAccountsAndSupplementaryGroupsAreExplicit()
    {
        var db = new AccountDatabase(Passwd, Group, Shadow); var account = db.Find("alice");
        Assert.NotNull(account); Assert.Equal("!", account!.Password); Assert.Equal("/home/alice", account.Home);
        var identity = db.Identity(account); Assert.Equal(1000u, identity.Uid); Assert.True(identity.InGroup(20));
        Assert.False(identity.IsRoot); Assert.Null(db.Find("ALICE"));
    }
    [Theory]
    [InlineData("../alice")] [InlineData("Alice")] [InlineData("alice:root")] [InlineData("0alice")] [InlineData("alice\nroot")]
    public void UnsafeNamesAreRejected(string name) => Assert.False(AccountDatabase.ValidName(name));
    [Fact]
    public void CorruptOrAmbiguousDatabasesFailClosed()
    {
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd + Passwd, Group, Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, Group + Group, Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, Group, Shadow + Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd.Replace("1000:1000", "0:1000"), Group, Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd.Replace("/home/alice", "/root"), Group, Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, "root:x:0:\n", Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, Group, "root:!:0:0:99999:7:::\n"));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, Group, Shadow.Replace("alice:!:", "alice::")));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, Group, new string('a', 65537)));
    }

    [Fact]
    public void UnauthenticatedIdentityCannotBeAssignedToAnAccountOrGroup()
    {
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd.Replace("1000:1000", "65534:1000"), Group, Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd, Group.Replace("1000", "65534"), Shadow));
        Assert.Throws<FormatException>(() => new AccountDatabase(Passwd.Replace("alice", "nobody"), Group.Replace("alice", "nobody"), Shadow.Replace("alice", "nobody")));
    }
}
