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
        add(new Command("sudo", SudoRequest.Usage, "Run a command as root (or -u user) after your own password; wheel members only", Sudo));
        add(new Command("groups", "groups [user]", "Print the groups an account belongs to", c =>
        {
            if (c.Args.Length > 1) { return c.Fail("invalid arguments; see help " + c.Name); }
            var service = Require(); string name = c.Args.Length == 1 ? c.Args[0] : service.Name(c.Shell.Identity.Uid);
            c.WriteLine(string.Join(" ", service.Groups(name))); return 0;
        }));
        add(new Command("gpasswd", "gpasswd -a|-d user group", "Add an account to a group or remove it (root; wheel = administrators)", c =>
        {
            if (c.Args.Length != 3 || (c.Args[0] != "-a" && c.Args[0] != "-d")) { return c.Fail("invalid arguments; see help " + c.Name); }
            bool add = c.Args[0] == "-a"; string user = c.Args[1], group = c.Args[2];
            bool changed = Require().SetMember(group, user, add);
            Zenith.Core.Log.Write("accounts", (add ? "added " : "removed ") + user + (add ? " to " : " from ") + group);
            c.WriteLine(changed ? (add ? "added " + user + " to " + group : "removed " + user + " from " + group)
                : user + (add ? " is already in " : " is not in ") + group);
            return 0;
        }));
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
    /// <summary>sudo: the invoking account proves itself with its own password (root never needs one),
    /// must be in wheel, and is remembered for five minutes in this terminal. The command runs in a
    /// child shell as the target account; native programs it starts inherit that identity.</summary>
    private static int Sudo(CommandContext c)
    {
        var request = SudoRequest.Parse(c.Args, out string? error);
        if (request is null) { return c.Fail(error!); }
        var shell = c.Shell; var caller = shell.Identity;
        if (request.Forget) { shell.Sudo.Forget(); }
        if (request.ForgetOnly) { return 0; }
        var service = Require(); string name = service.Name(caller.Uid);
        if (!service.IsAdministrator(caller.Uid))
        {
            Zenith.Core.Log.Write("sudo", name + " : not in wheel ; COMMAND=" + Describe(request.Command));
            if (request.List) { c.WriteLine(name + " may not run sudo on this computer."); return 1; }
            return c.Fail(name + " is not an administrator (wheel group); ask one to run: gpasswd -a " + name + " wheel");
        }
        if (!caller.IsRoot && !shell.Sudo.Valid(caller.Uid))
        {
            if (request.NonInteractive) { return c.Fail("a password is required"); }
            if (shell.IsBackground || shell.ReadSecret is null) { return c.Fail("a password is required, and this is not an interactive terminal"); }
            bool passed = false;
            for (int attempt = 0; attempt < 3 && !passed; attempt++)
            {
                string? password = shell.ReadSecret("[sudo] password for " + name + ": ");
                if (password is null) { return 130; }
                passed = service.Authenticate(name, password) is Credentials proven && proven.Uid == caller.Uid;
                password = string.Empty;
                if (!passed) { Thread.Sleep(1000); if (attempt < 2) { c.Err.Write("Sorry, try again.\n"); } }
            }
            if (!passed)
            {
                Zenith.Core.Log.Write("sudo", name + " : 3 incorrect password attempts ; COMMAND=" + Describe(request.Command));
                return c.Fail("3 incorrect password attempts");
            }
            shell.Sudo.Grant(caller.Uid);
        }
        else if (!caller.IsRoot) { shell.Sudo.Grant(caller.Uid); }
        if (request.Validate) { return 0; }
        if (request.List)
        {
            c.WriteLine(name + " may run any command as any user (wheel group).");
            return 0;
        }
        Credentials target; UserAccount account;
        using (CredentialContext.Enter(Credentials.Root))
        {
            account = service.Find(request.User) ?? throw new ArgumentException("unknown user " + request.User);
            target = service.RootSwitch(request.User);
        }
        Zenith.Core.Log.Write("sudo", name + " : USER=" + account.Name + " ; PWD=" + shell.WorkingDirectory + " ; COMMAND=" + Describe(request.Command));
        var environment = new System.Collections.Generic.Dictionary<string, string>
        {
            ["USER"] = account.Name, ["LOGNAME"] = account.Name, ["HOME"] = account.Home,
            ["SUDO_USER"] = name, ["SUDO_UID"] = caller.Uid.ToString(), ["SUDO_GID"] = caller.Gid.ToString(),
            ["SUDO_COMMAND"] = Describe(request.Command),
        };
        return shell.RunAs(target, environment, request.Command, c.Input, c.Out);
    }
    private static string Describe(System.Collections.Generic.List<string> command)
    {
        string text = command.Count == 0 ? "(validate)" : string.Join(" ", command);
        return text.Length > 200 ? text[..200] + "..." : text;
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
