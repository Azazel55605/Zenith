using System;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Portal;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell.Portal;

/// <summary>org.zenith.portal.Authorization: "Program wants to manage disks and drives". The approver
/// (the signed-in administrator, else root) types their password; three wrong ones deny the request.
/// Escape or Cancel denies it too. The password never leaves this dialog and the account service.</summary>
internal sealed class AuthorizationDialog : IPortalDialog
{
    private readonly PortalRequest _request;
    private readonly AuthorizationPrompt _prompt;
    private readonly StringBuilder _password = new();
    private readonly LoginPrompt _login = new((name, password) => Accounts.Service?.Authenticate(name, password));
    private int _failures;
    private string _message = "";
    private Rect _field, _cancel, _submit;

    public AuthorizationDialog(PortalRequest request)
    {
        _request = request;
        _prompt = request.Prompt as AuthorizationPrompt ?? throw new ArgumentException("authorization request without a prompt");
    }

    public string Heading => "Authentication required";
    public bool Done { get; private set; }

    private string Sentence => _request.Caller.Program + " wants to " + _prompt.Action.Title + ".";
    private string Who => _prompt.OwnPassword ? "Enter your password (" + _prompt.Approver + ") to allow this." : "An administrator must allow this. Enter the root password.";

    public int Height(int width)
    {
        int lines = PortalHost.Wrap(Sentence, Fonts.SemiBold, Theme.TextBody, width).Count;
        int detail = PortalHost.Wrap(_prompt.Action.Description, Fonts.Regular, Theme.TextSmall, width).Count;
        return lines * 22 + 6 + detail * 18 + 14 + 26 + AppStyle.ControlHeight + 8 + 22 + AppStyle.ControlHeight;
    }

    public void Draw(Surface surface, Rect body, int mouseX, int mouseY)
    {
        int y = body.Y;
        foreach (string line in PortalHost.Wrap(Sentence, Fonts.SemiBold, Theme.TextBody, body.W))
        { surface.DrawText(line, Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, body.X, y); y += 22; }
        y += 6;
        foreach (string line in PortalHost.Wrap(_prompt.Action.Description, Fonts.Regular, Theme.TextSmall, body.W))
        { surface.DrawText(line, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, body.X, y); y += 18; }
        y += 14;
        surface.DrawText(PortalHost.Fit(Who, body.W, Theme.TextSmall), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, body.X, y);
        y += 26;
        _field = new Rect(body.X, y, body.W, AppStyle.ControlHeight);
        string dots = new('*', Math.Min(_password.Length, 32));
        AppStyle.Field(surface, _field, dots, true);
        if (!_login.Busy)
        {
            int caret = _field.X + 12 + Fonts.Regular.MeasureString(dots, Theme.TextBody);
            surface.FillRect(new Rect(Math.Min(caret, _field.Right - 10), _field.Y + 9, 2, _field.H - 18), Theme.Accent);
        }
        y += AppStyle.ControlHeight + 8;
        string status = _login.Busy ? "Checking..." : _message;
        if (status.Length > 0) { surface.DrawText(status, Fonts.Regular, Theme.TextSmall, _login.Busy ? Theme.TextSecondary : Theme.Danger, body.X, y); }
        y += 22;
        _submit = new Rect(body.Right - 132, y, 132, AppStyle.ControlHeight);
        _cancel = new Rect(_submit.X - 8 - 100, y, 100, AppStyle.ControlHeight);
        AppStyle.Button(surface, _cancel, "Cancel", enabled: !_login.Busy);
        AppStyle.Button(surface, _submit, "Authenticate", primary: true, enabled: !_login.Busy && _password.Length > 0);
        if (!_login.Busy && _cancel.Contains(mouseX, mouseY)) { surface.FillRoundRect(_cancel, Theme.ControlRadius, Theme.Hover); }
        if (!_login.Busy && _password.Length > 0 && _submit.Contains(mouseX, mouseY)) { surface.FillRoundRect(_submit, Theme.ControlRadius, Theme.Hover); }
    }

    public void Key(KeyEvent key)
    {
        if (_login.Busy || Done) { return; }
        if (key.Key == ConsoleKeyEx.Escape) { Cancel(); return; }
        if (key.Key == ConsoleKeyEx.Enter) { Submit(); return; }
        if (key.Key == ConsoleKeyEx.Backspace) { if (_password.Length > 0) { _password.Length--; } return; }
        if (key.KeyChar >= ' ' && !char.IsControl(key.KeyChar) && _password.Length < 256) { _password.Append(key.KeyChar); _message = ""; }
    }

    public void Click(int x, int y)
    {
        if (_login.Busy || Done) { return; }
        if (_cancel.Contains(x, y)) { Cancel(); }
        else if (_submit.Contains(x, y)) { Submit(); }
    }

    public long HoverKey(int x, int y) => _cancel.Contains(x, y) ? 1 : _submit.Contains(x, y) ? 2 : 0;

    public bool Tick()
    {
        if (!_login.TakeResult(out var identity)) { return false; }
        if (identity is not null)
        {
            Done = true;
            if (!Portals.Authorization.Approve(_request, identity.Uid, _prompt.Approver)) { Zenith.Core.Log.Write("portal", "authorization not granted after authentication"); }
            return true;
        }
        if (++_failures >= AuthorizationPortal.Attempts)
        {
            Done = true; Portals.Authorization.Deny(_request, AuthorizationPortal.Attempts + " incorrect password attempts");
            return true;
        }
        _message = "Incorrect password. Try again.";
        Zenith.Core.Log.Write("portal", "authorization password incorrect (" + _failures + ")");
        return true;
    }

    private void Submit()
    {
        if (_password.Length == 0) { return; }
        string password = _password.ToString(); _password.Clear();
        _login.Submit(_prompt.Approver, password);
    }

    private void Cancel() { Done = true; Portals.Authorization.Deny(_request, "dismissed"); }
}
