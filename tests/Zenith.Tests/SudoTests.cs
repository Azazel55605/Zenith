using System;
using System.Collections.Generic;
using Zenith.Core.Security;
using Zenith.Core.Shell;

namespace Zenith.Tests;

public class SudoTests
{
    [Fact]
    public void ParsesOptionsUntilTheCommand()
    {
        var r = SudoRequest.Parse(new[] { "-n", "-u", "alice", "ls", "-l", "/root" }, out var error)!;
        Assert.Null(error); Assert.True(r.NonInteractive); Assert.Equal("alice", r.User);
        Assert.Equal(new[] { "ls", "-l", "/root" }, r.Command);
        r = SudoRequest.Parse(new[] { "-ualice", "--", "-weird" }, out _)!;
        Assert.Equal("alice", r.User); Assert.Equal(new[] { "-weird" }, r.Command);
        r = SudoRequest.Parse(new[] { "--user=bob", "id" }, out _)!;
        Assert.Equal("bob", r.User); Assert.Equal("root", SudoRequest.Parse(new[] { "id" }, out _)!.User);
    }

    [Fact]
    public void ModesAndMistakes()
    {
        Assert.True(SudoRequest.Parse(new[] { "-k" }, out _)!.ForgetOnly);
        var both = SudoRequest.Parse(new[] { "-k", "ls" }, out _)!;
        Assert.True(both.Forget); Assert.False(both.ForgetOnly);
        Assert.True(SudoRequest.Parse(new[] { "-v" }, out _)!.Validate);
        Assert.True(SudoRequest.Parse(new[] { "-l" }, out _)!.List);
        Assert.Null(SudoRequest.Parse(Array.Empty<string>(), out var none)); Assert.Contains("no command", none);
        Assert.Null(SudoRequest.Parse(new[] { "-u" }, out _));
        Assert.Null(SudoRequest.Parse(new[] { "-x", "ls" }, out var unknown)); Assert.Contains("-x", unknown);
        Assert.Null(SudoRequest.Parse(new[] { "-v", "ls" }, out _));
        Assert.Null(SudoRequest.Parse(new[] { "-v", "-l" }, out _));
    }

    [Fact]
    public void TicketLastsFiveMinutesForOneAccount()
    {
        long now = 1000; var ticket = new SudoTicket(() => now);
        Assert.False(ticket.Valid(1000));
        ticket.Grant(1000);
        Assert.True(ticket.Valid(1000)); Assert.False(ticket.Valid(1001));
        now += SudoTicket.LifetimeMilliseconds - 1; Assert.True(ticket.Valid(1000));
        now += 1; Assert.False(ticket.Valid(1000));
        ticket.Grant(1000); ticket.Forget(); Assert.False(ticket.Valid(1000));
    }

    [Fact]
    public void RunAsUsesTheTargetIdentityOnlyForThatCommand()
    {
        using var shell = new ShellHarness();
        var seen = new List<(uint Uid, string User, string SudoUser)>();
        Shell.Register(new Command("sudo-probe", "sudo-probe", "test", c =>
        {
            seen.Add((CredentialContext.Current.Uid, c.Shell.Get("USER"), c.Shell.Get("SUDO_USER")));
            return 7;
        }));
        var env = new Dictionary<string, string> { ["USER"] = "root", ["SUDO_USER"] = "user" };
        using (CredentialContext.Enter(new Credentials(1000, 1000)))
        {
            int status = shell.Shell.RunAs(Credentials.Root, env, new List<string> { "sudo-probe" }, null, new BufferOutput());
            Assert.Equal(7, status);
        }
        Assert.Equal((0u, "root", "user"), seen[0]);
        shell.Run("sudo-probe");
        Assert.NotEqual("root", seen[1].User);
        Assert.Equal("", seen[1].SudoUser);
    }
}

public class AdministratorTests
{
    private sealed class Store : IAccountStore
    {
        public Dictionary<string, string> Files = new()
        {
            ["passwd"] = "root:x:0:0:root:/root:/bin/sh\nuser:x:1000:1000:User:/home/user:/bin/sh\nbob:x:1001:1001:bob:/home/bob:/bin/sh\n",
            ["group"] = "root:x:0:\nusers:x:100:user\nuser:x:1000:\nbob:x:1001:\n",
            ["shadow"] = "root:!:::::::\nuser:!:::::::\nbob:!:::::::\n"
        };
        public string Read(string name) => Files[name];
        public void Replace(string name, string text) => Files[name] = text;
        public void CreateHome(string name, uint uid, uint gid) { }
    }
    private sealed class Entropy : IEntropySource { public bool TryFill(Span<byte> bytes) { bytes.Fill(7); return true; } }

    [Fact]
    public void WheelIsCreatedOnceWithTheInstallersAccount()
    {
        var store = new Store(); var service = new AccountService(store, new Entropy());
        Assert.False(service.IsAdministrator(1000)); Assert.True(service.IsAdministrator(0));
        using (CredentialContext.Enter(new Credentials(1000, 1000))) { Assert.Throws<UnauthorizedAccessException>(() => service.EnsureAdministrators()); }
        using (CredentialContext.Enter(Credentials.Root))
        {
            Assert.True(service.EnsureAdministrators());
            Assert.Contains("wheel:x:10:user\n", store.Files["group"]);
            Assert.False(service.EnsureAdministrators());   // never a second time, never re-adding a removed member
            service.SetMember("wheel", "user", false);
            Assert.False(service.EnsureAdministrators());
        }
        Assert.False(service.IsAdministrator(1000));
    }

    [Fact]
    public void MembershipChangesApplyAtOnce()
    {
        var store = new Store(); var service = new AccountService(store, new Entropy());
        using var root = CredentialContext.Enter(Credentials.Root);
        service.EnsureAdministrators();
        Assert.True(service.IsAdministrator(1000)); Assert.False(service.IsAdministrator(1001));
        Assert.True(service.SetMember("wheel", "bob", true));
        Assert.False(service.SetMember("wheel", "bob", true));
        Assert.True(service.IsAdministrator(1001));
        Assert.Equal(new[] { "bob", "wheel" }, service.Groups("bob"));
        Assert.Throws<ArgumentException>(() => service.SetMember("bob", "bob", false));   // primary group
        Assert.Throws<ArgumentException>(() => service.SetMember("nogroup", "bob", true));
        Assert.True(service.SetMember("wheel", "bob", false));
        Assert.False(service.IsAdministrator(1001));
        Assert.Contains("wheel:x:10:user\n", store.Files["group"]);
    }

    [Fact]
    public void InstalledAccountsAdministerTheSystem()
    {
        var profile = Zenith.Core.Storage.InstallationProfile.Create("box", "alice", "Root-pass-1", "Root-pass-1", "User-pass-1", "User-pass-1", new Entropy());
        var db = new AccountDatabase(profile.Passwd, profile.Group, profile.Shadow);
        Assert.Equal(new[] { "alice" }, db.Members(10));
        Assert.True(db.Identity(db.Find("alice")!).InGroup(10));
    }
}
