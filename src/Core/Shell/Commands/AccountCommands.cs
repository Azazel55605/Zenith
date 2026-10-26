using System;
using System.Threading;
using Zenith.Core.Security;

namespace Zenith.Core.Shell.Commands;

internal static class AccountCommands
{
    public static void Register(Action<Command> add)
    {
        add(new Command("whoami", "whoami", "Print the authenticated user", c => { c.WriteLine(Accounts.Service?.Name(c.Shell.Identity.Uid) ?? (c.Shell.Identity.IsRoot ? "root" : "nobody")); return 0; }));
        add(new Command("id", "id", "Print authenticated IDs", c => { var id = c.Shell.Identity; c.WriteLine("uid=" + id.Uid + "(" + (Accounts.Service?.Name(id.Uid) ?? (id.IsRoot ? "root" : id.Uid == 65534 ? "nobody" : id.Uid.ToString())) + ") gid=" + id.Gid); return 0; }));
        add(new Command("login", "login user", "Authenticate the current session", c => Switch(c, true)));
        add(new Command("su", "su [user]", "Switch this terminal's identity", c => Switch(c, false)));
        add(new Command("logout", "logout", "Return to login and session selection", c =>
        {
            if (c.Shell.IsBackground || c.Shell.HasBackgroundJobs) { return c.Fail("stop or wait for background jobs before changing account"); }
            if (Accounts.Service is null) { return c.Fail("accounts require an ext2 installation"); }
            if (Zenith.Core.Sessions.SessionRuntime.DesktopAvailable())
            {
                string? error = Zenith.Core.Sessions.SessionRuntime.RequestLogout(c.Shell);
                return error is null ? 0 : c.Fail(error);
            }
            Accounts.Logout(); c.Shell.SetIdentity(Credentials.Nobody, "nobody", "/"); return 0;
        }));
        add(new Command("passwd", "passwd [user]", "Set a password using hidden prompts", Password));
        add(new Command("useradd", "useradd user", "Create a locked account and private home", c => { if (c.Args.Length != 1) { return c.Fail("invalid arguments; see help " + c.Name); } var service = Require(); uint id = service.Add(c.Args[0]); c.WriteLine("created " + c.Args[0] + " uid=" + id + "; use passwd " + c.Args[0] + " to unlock"); return 0; }));
    }
    private static AccountService Require() => Accounts.Service ?? throw new InvalidOperationException(Accounts.Error ?? "accounts require an ext2 installation");
    private static string? Secret(CommandContext c, string prompt)
    {
        if (!c.IsTerminal || c.Input is not null || c.Shell.ReadSecret is null) { throw new InvalidOperationException("passwords require an interactive terminal"); }
        return c.Shell.ReadSecret(prompt);
    }
    private static int Switch(CommandContext c, bool login)
    {
        if (c.Args.Length > 1 || (login && c.Args.Length != 1)) { return c.Fail("invalid arguments; see help " + c.Name); }
        if (c.Shell.IsBackground || c.Shell.HasBackgroundJobs) { return c.Fail("stop or wait for background jobs before changing account"); }
        var service = Require(); string name = c.Args.Length == 0 ? "root" : c.Args[0];
        if (login && Zenith.Core.Sessions.SessionRuntime.DesktopAvailable())
        {
            string? blocked = Zenith.Core.Sessions.SessionRuntime.ChangeBlocker(c.Shell);
            if (blocked is not null) { return c.Fail(blocked); }
        }
        Credentials? identity;
        if (!login && c.Shell.Identity.IsRoot) { identity = service.RootSwitch(name); }
        else
        {
            string? password = Secret(c, "Password: ");
            if (password is null) { return 130; }
            identity = service.Authenticate(name, password);
            if (identity is null) { Thread.Sleep(1000); return c.Fail("authentication failed"); }
        }
        var user = service.Find(name)!;
        c.Shell.SetIdentity(identity, user.Name, user.Home);
        if (login) { Accounts.Login(identity); }
        c.WriteLine("authenticated as " + name);
        return 0;
    }
    private static int Password(CommandContext c)
    {
        if (c.Args.Length > 1) { return c.Fail("invalid arguments; see help " + c.Name); }
        var service = Require(); string name = c.Args.Length == 0 ? service.Name(c.Shell.Identity.Uid) : c.Args[0];
        var user = service.Find(name) ?? throw new ArgumentException("unknown user");
        if (!c.Shell.Identity.IsRoot && c.Shell.Identity.Uid != user.Uid) { return c.Fail("permission denied"); }
        string? old = null;
        if (!c.Shell.Identity.IsRoot) { old = Secret(c, "Current password: "); if (old is null) { return 130; } }
        string? first = Secret(c, "New password: "); if (first is null) { return 130; }
        string? second = Secret(c, "Repeat password: "); if (second is null) { return 130; }
        if (first != second) { return c.Fail("passwords do not match; unchanged"); }
        service.SetPassword(name, old, first);
        c.WriteLine("password updated for " + name);
        return 0;
    }
}
