using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Zenith.Core.Security;

namespace Zenith.Tests;

public class SecurityTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(55)] [InlineData(56)] [InlineData(63)]
    [InlineData(64)] [InlineData(65)] [InlineData(127)] [InlineData(128)] [InlineData(1024)]
    public void Sha256MatchesPlatformAcrossPaddingAndBlockBoundaries(int length)
    {
        byte[] input = new byte[length]; new Random(12).NextBytes(input);
        byte[] actual = new byte[32]; Sha256.Hash(input, actual);
        Assert.Equal(SHA256.HashData(input), actual);
    }

    [Theory]
    [InlineData("password", 1)] [InlineData("password", 2)] [InlineData("password", 4096)]
    [InlineData("pässwort🔑", 3)] [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 5)]
    public void Pbkdf2MatchesPlatformIncludingLongAndUtf8Keys(string password, int rounds)
    {
        byte[] salt = Encoding.UTF8.GetBytes("salt"); byte[] actual = new byte[32];
        PasswordHash.Derive(password, salt, rounds, actual);
        Assert.Equal(Rfc2898DeriveBytes.Pbkdf2(password, salt, rounds, HashAlgorithmName.SHA256, 32), actual);
    }

    [Fact]
    public void ProductionRecordMatchesPlatformAndRejectsWrongPassword()
    {
        var entropy = new SeedEntropySource((out ulong word) => { word = 0x1234567890abcdef; return true; });
        string record = PasswordHash.Create("test password", entropy);
        string[] fields = record.Split('$');
        Assert.Equal(Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2("test password", Convert.FromBase64String(fields[3]),
            600000, HashAlgorithmName.SHA256, 32)), fields[4]);
        Assert.True(PasswordHash.Verify("test password", record));
        Assert.False(PasswordHash.Verify("wrong password", record));
        Assert.False(PasswordHash.Verify("test password", record.Replace("600000", "1")));
        Assert.False(PasswordHash.Verify("test password", record.Replace("600000", "999999999")));
        Assert.False(PasswordHash.Verify("test password", record.Replace("sha256", "unknown")));
        Assert.False(PasswordHash.Verify("test password", record + "extra"));
        Assert.False(PasswordHash.Verify("", record));
    }

    [Fact]
    public void EntropyFailureErasesPartialOutputAndNeverCreatesPassword()
    {
        int calls = 0;
        var entropy = new SeedEntropySource((out ulong word) => { word = ulong.MaxValue; return ++calls == 1; });
        byte[] buffer = new byte[16];
        Assert.False(entropy.TryFill(buffer)); Assert.Equal(new byte[16], buffer); Assert.Equal(257, calls);
        Assert.Throws<InvalidOperationException>(() => PasswordHash.Create("secret", entropy));
    }

    [Fact]
    public void EntropyRetriesAndCopiesOnlyRequestedBytes()
    {
        int calls = 0;
        var entropy = new SeedEntropySource((out ulong word) => { word = 0x0807060504030201; return ++calls % 3 == 0; });
        byte[] bytes = new byte[9]; Assert.True(entropy.TryFill(bytes));
        Assert.Equal(new byte[] {1,2,3,4,5,6,7,8,1}, bytes); Assert.Equal(6, calls);
        Assert.True(entropy.TryFill(Span<byte>.Empty));
        Assert.False(entropy.TryFill(new byte[4097]));
    }

    [Fact]
    public void IdentityIsImmutableAndDoesNotLeakAcrossWorkersOrNestedScopes()
    {
        Assert.Same(Credentials.Nobody, CredentialContext.Current);
        uint[] groups = { 77 }; var owner = new Credentials(1000, 100, groups); groups[0] = 88;
        Assert.True(owner.InGroup(77)); Assert.False(owner.InGroup(88));
        using (CredentialContext.Enter(owner))
        {
            using (CredentialContext.Enter(Credentials.Root)) { Assert.True(CredentialContext.Current.IsRoot); }
            Assert.Same(owner, CredentialContext.Current);
            Credentials? worker = null; var thread = new Thread(() => worker = CredentialContext.Current);
            thread.Start(); thread.Join(); Assert.Same(Credentials.Nobody, worker);
        }
        Assert.Same(Credentials.Nobody, CredentialContext.Current);
    }

    [Fact]
    public void PermissionClassSelectionDoesNotFallBackToMorePermissiveOtherBits()
    {
        var owner = new Credentials(1000, 100); var group = new Credentials(1001, 100);
        var other = new Credentials(1002, 200); var supplementary = new Credentials(1003, 200, 100);
        Assert.False(PermissionPolicy.Allows(owner, 1000, 100, 0x004, PermissionPolicy.Read));
        Assert.False(PermissionPolicy.Allows(group, 1000, 100, 0x004, PermissionPolicy.Read));
        Assert.True(PermissionPolicy.Allows(other, 1000, 100, 0x004, PermissionPolicy.Read));
        Assert.True(PermissionPolicy.Allows(supplementary, 1000, 100, 0x020, PermissionPolicy.Read));
        Assert.False(PermissionPolicy.Allows(supplementary, 1000, 100, 0x020, PermissionPolicy.Read | PermissionPolicy.Write));
        Assert.True(PermissionPolicy.Allows(Credentials.Root, 1000, 100, 0, 7));
        Assert.False(PermissionPolicy.Allows(Credentials.Root, 1000, 100, 0, 8));
    }

    [Fact]
    public void StickyRemovalOwnershipAndSetgidCreationPolicy()
    {
        var user = new Credentials(1000, 100);
        Assert.False(PermissionPolicy.CanRemove(user, 0, 0, 0x3ff, 2000));
        Assert.True(PermissionPolicy.CanRemove(user, 0, 0, 0x3ff, 1000));
        Assert.False(PermissionPolicy.CanRemove(user, 0, 0, 0x3fe, 1000));
        Assert.True(PermissionPolicy.CanChmod(user, 1000)); Assert.False(PermissionPolicy.CanChmod(user, 0));
        Assert.False(PermissionPolicy.CanChown(user));
        Assert.Equal((ushort)0x5ed, PermissionPolicy.CreationMode(0xfff, 0x12, 0x400, true));
        Assert.Equal((ushort)0x1ed, PermissionPolicy.CreationMode(0xfff, 0x12, 0x400, false));
        Assert.Equal(200u, PermissionPolicy.CreationGroup(user, 200, 0x400));
        Assert.Equal(100u, PermissionPolicy.CreationGroup(user, 200, 0));
    }
}
