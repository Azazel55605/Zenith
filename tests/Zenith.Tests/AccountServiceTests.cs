using System;
using System.Collections.Generic;
using Zenith.Core.Security;

namespace Zenith.Tests;

public class AccountServiceTests
{
    [Fact]
    public void ConfiguredUserWithLockedRootStillRequiresLogin()
    {
        var service = new AccountService(new Store(), new Entropy());
        Assert.False(service.HasPasswords);
        using (CredentialContext.Enter(Credentials.Root)) { service.SetPassword("user", null, "User-password-1"); }
        Assert.True(service.HasPasswords); Assert.False(service.RootConfigured);
        Assert.Null(service.Authenticate("root", "User-password-1"));
        Assert.Equal(1000u, service.Authenticate("user", "User-password-1")!.Uid);
    }
    [Fact]
    public void PublicAccountSummaryHasNoSecretsAndOrdinaryReadersSeeOnlyThemselves()
    {
        var service=new AccountService(new Store(),new Entropy());
        using(CredentialContext.Enter(Credentials.Root)){string text=service.PublicSummary();Assert.Contains("root  uid=0",text);Assert.Contains("user  uid=1000",text);Assert.DoesNotContain("!",text);}
        using(CredentialContext.Enter(new Credentials(1000,1000))){string text=service.PublicSummary();Assert.DoesNotContain("/root",text);Assert.Contains("/home/user",text);}
    }
    private sealed class Store : IAccountStore
    {
        public Dictionary<string,string> Files = new()
        {
            ["passwd"] = "root:x:0:0:root:/root:/bin/sh\nuser:x:1000:1000:User:/home/user:/bin/sh\n",
            ["group"] = "root:x:0:\nuser:x:1000:\n",
            ["shadow"] = "root:!:::::::\nuser:!:::::::\n"
        };
        public string? Fail;
        public List<string> Writes = new();
        public string Read(string name) => Files[name];
        public void Replace(string name, string text)
        {
            Writes.Add(name);
            if (name == Fail) { throw new InvalidOperationException("injected write failure"); }
            Files[name] = text;
        }
        public void CreateHome(string name, uint uid, uint gid) { Writes.Add("home"); if (Fail == "home") { throw new InvalidOperationException("injected home failure"); } }
    }
    private sealed class Entropy : IEntropySource
    {
        public bool Available = true;
        public bool TryFill(Span<byte> bytes) { bytes.Fill(0x42); return Available; }
    }
    [Fact]
    public void LockedAccountsAndRootOnlyCreation()
    {
        var store = new Store(); var service = new AccountService(store, new Entropy());
        Assert.False(service.RootConfigured);
        Assert.Null(service.Authenticate("root", "anything"));
        using (CredentialContext.Enter(new Credentials(1000,1000))) { Assert.Throws<UnauthorizedAccessException>(() => service.Add("alice")); }
        using (CredentialContext.Enter(Credentials.Root))
        {
            Assert.Equal(1001u,service.Add("alice"));
            Assert.Equal(new[]{"group","shadow","home","passwd"},store.Writes);
            Assert.Equal(1001u,service.RootSwitch("alice").Uid);
            Assert.Null(service.Authenticate("alice","anything"));
        }
    }
    [Theory]
    [InlineData("group")]
    [InlineData("shadow")]
    [InlineData("home")]
    [InlineData("passwd")]
    public void FailedAdditionNeverPublishesAuthenticatableUser(string failure)
    {
        var store = new Store { Fail = failure }; var service = new AccountService(store,new Entropy());
        using var root = CredentialContext.Enter(Credentials.Root);
        Assert.Throws<InvalidOperationException>(() => service.Add("alice"));
        Assert.Null(service.Find("alice"));
        Assert.Null(service.Authenticate("alice", "password"));
    }
    [Fact]
    public void PasswordChangesAuthorizeBeforeHashingAndEntropyFailurePreservesRecord()
    {
        var store = new Store(); var entropy = new Entropy(); var service = new AccountService(store, entropy);
        using (CredentialContext.Enter(new Credentials(1000,1000)))
        {
            Assert.Throws<UnauthorizedAccessException>(() => service.SetPassword("root",null,"secret"));
            Assert.Throws<UnauthorizedAccessException>(() => service.SetPassword("user","wrong","secret"));
        }
        using var root = CredentialContext.Enter(Credentials.Root);
        entropy.Available = false;
        string before = store.Read("shadow");
        Assert.Throws<InvalidOperationException>(() => service.SetPassword("root",null,"secret"));
        Assert.Equal(before,store.Read("shadow")); Assert.Empty(store.Writes);
    }
    [Fact]
    public void PasswordAuthenticationAndSelfChangePreserveIdentity()
    {
        var store = new Store(); var service = new AccountService(store,new Entropy());
        using (CredentialContext.Enter(Credentials.Root)) { service.SetPassword("user",null,"first password"); }
        var user = service.Authenticate("user","first password"); Assert.NotNull(user); Assert.Equal(1000u,user.Uid);
        using (CredentialContext.Enter(user)) { service.SetPassword("user","first password","second password"); }
        Assert.Null(service.Authenticate("user","first password"));
        Assert.Equal(1000u,service.Authenticate("user","second password")!.Uid);
        Assert.DoesNotContain("second password",store.Read("shadow"));
        Assert.Null(service.Authenticate("user","bad\0password"));
    }
}
