using System;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;
using Zenith.Core;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Gui.Graphics;
using CommandShell = Zenith.Core.Shell.Shell;

namespace Zenith.Gui.Shell;

internal sealed class LoginSessionHost : ISessionHost
{
    private readonly LoginPrompt _login = new((name, password) => Accounts.Service?.Authenticate(name, password));
    private readonly StringBuilder _name = new();
    private readonly StringBuilder _password = new();
    private readonly Input _input = new();
    private SessionMode _mode;
    private int _focus;
    private Surface _surface = null!;
    private Surface _background = null!;
    private string _title = "Zenith", _subtitle = "Sign in to your workspace";
    private bool _dirty = true;
    public LoginSessionHost(SessionMode selected) => _mode = selected;
    public bool CanStop => !_login.Busy;
    public string? StopBlocker(CommandShell? requester) => _login.Busy ? "Authentication is running." : null;
    public void Stop() { _name.Clear(); _password.Clear(); _surface = null!; _background = null!; }
    public void BeginStop() { }
    public void Start()
    {
        NativeAppearance.Reload();
        var screen = SessionDisplay.Screen; _surface = new Surface(screen.Width, screen.Height);
        _background = new Surface(screen.Width, screen.Height); Wallpaper.Render(_background);
        string? branding = NativeAppearance.Resource("/usr/share/zenith/session/login.conf");
        if (branding is not null && branding.StartsWith("VERSION=1\n", StringComparison.Ordinal) && branding.Length < 512)
        {
            foreach (string line in branding.Split('\n'))
            {
                string value = line.Length > 6 && line.StartsWith("TITLE=", StringComparison.Ordinal) ? line[6..] :
                    line.Length > 9 && line.StartsWith("SUBTITLE=", StringComparison.Ordinal) ? line[9..] : "";
                bool valid = value.Length is > 0 and <= 40;
                foreach (char c in value) { valid &= c >= 32 && c <= 126; }
                if (valid) { if (line.StartsWith("TITLE=", StringComparison.Ordinal)) { _title = value; } else { _subtitle = value; } }
            }
        }
        MouseManager.SetScreenSize(screen.Width, screen.Height);
        Draw(); _dirty = false;
        Log.Write("session", "login ready; selection=" + SessionModes.Name(_mode));
    }
    private Rect Card => new((_surface.Width - Math.Min(460, _surface.Width - 32)) / 2, Math.Max(8, (_surface.Height - 460) / 2), Math.Min(460, _surface.Width - 32), 460);
    private Rect Field(int index) => new(Card.X + 28, Card.Y + 120 + index * 76, Card.W - 56, 44);
    private Rect Selector => new(Card.X + 28, Card.Y + 288, Card.W - 56, 44);
    private Rect Submit => new(Card.X + 28, Card.Y + 350, Card.W - 56, 46);
    private Rect Restart => new(_surface.Width / 2 - 130, _surface.Height - 56, 120, 34);
    private Rect Shutdown => new(_surface.Width / 2 + 10, _surface.Height - 56, 120, 34);
    public void Tick()
    {
        if (_login.TakeResult(out var identity))
        {
            _dirty = true;
            if (identity is not null) { SessionRuntime.Activate(identity, _mode); return; }
            Log.Write("session", "authentication failed");
        }
        _input.Poll();
        _dirty |= _input.Moved;
        if (!_login.Busy)
        {
            if (_input.LeftPressed)
            {
                if (Restart.Contains(_input.X, _input.Y) || Shutdown.Contains(_input.X, _input.Y))
                {
                    using var root = CredentialContext.Enter(Credentials.Root);
                    try { if (Restart.Contains(_input.X, _input.Y)) { Zenith.Core.PowerControl.Reboot(); } else { Zenith.Core.PowerControl.PowerOff(); } }
                    catch (Exception e) { Zenith.Core.Log.Write("session", "power action failed: " + e.Message); }
                    return;
                }
                if (Field(0).Contains(_input.X, _input.Y)) { _focus = 0; }
                else if (Field(1).Contains(_input.X, _input.Y)) { _focus = 1; }
                else if (Selector.Contains(_input.X, _input.Y)) { _focus = 2; Toggle(); }
                else if (Submit.Contains(_input.X, _input.Y)) { SignIn(); }
                _dirty = true;
            }
            foreach (KeyEvent key in _input.Keys)
            {
                if (_login.Busy) { break; }
                if (key.Key == ConsoleKeyEx.Tab) { _focus = (_focus + ((key.Modifiers & ConsoleModifiers.Shift) != 0 ? 2 : 1)) % 3; }
                else if (key.Key == ConsoleKeyEx.Enter) { if (_focus == 0) { _focus = 1; } else { SignIn(); } }
                else if (_focus == 2 && key.Key is ConsoleKeyEx.LeftArrow or ConsoleKeyEx.RightArrow or ConsoleKeyEx.Spacebar) { Toggle(); }
                else if (key.Key == ConsoleKeyEx.Backspace) { var text = _focus == 0 ? _name : _password; if (_focus < 2 && text.Length > 0) { text.Length--; } }
                else if (_focus < 2 && key.KeyChar >= ' ' && !char.IsControl(key.KeyChar))
                { var text = _focus == 0 ? _name : _password; if (text.Length < (_focus == 0 ? 32 : 256)) { text.Append(key.KeyChar); } }
                _dirty = true;
            }
        }
        if (!_dirty) { return; }
        Draw(); _dirty = false;
    }
    private void Toggle()
    {
        SessionMode next = _mode == SessionMode.Desktop ? SessionMode.Headless : SessionMode.Desktop;
        if (SessionRuntime.Available(next)) { _mode = next; }
    }
    private void SignIn()
    {
        string password = _password.ToString(); _password.Clear();
        _login.Submit(_name.ToString(), password); _dirty = true;
    }
    private void Draw()
    {
        _surface.CopyFrom(_background);
        _surface.FillRoundRect(Card, 16, Theme.WindowBackground);
        _surface.StrokeRoundRect(Card, 16, Theme.WindowBorder);
        _surface.DrawText(_title, Fonts.SemiBold, 30, Theme.TextPrimary, Card.X + 28, Card.Y + 24);
        _surface.DrawText(_subtitle, Fonts.Regular, 14, Theme.TextSecondary, Card.X + 28, Card.Y + 72);
        for (int i = 0; i < 2; i++)
        {
            Rect field = Field(i);
            _surface.DrawText(i == 0 ? "Username" : "Password", Fonts.Regular, 12, Theme.TextSecondary, field.X, field.Y - 20);
            _surface.FillRoundRect(field, 6, Theme.Panel);
            _surface.StrokeRoundRect(field, 6, _focus == i ? Theme.Accent : Theme.WindowBorder);
            string text = i == 0 ? _name.ToString() : new string('*', Math.Min(_password.Length, 32));
            _surface.DrawText(text, Fonts.Regular, 16, Theme.TextPrimary, field.X + 12, field.Y + 12);
        }
        _surface.FillRoundRect(Selector, 6, Theme.Panel);
        _surface.StrokeRoundRect(Selector, 6, _focus == 2 ? Theme.Accent : Theme.WindowBorder);
        _surface.DrawText("Session: " + (_mode == SessionMode.Desktop ? "Desktop" : "Headless (local console)") + "  < >", Fonts.Regular, 14, Theme.TextPrimary, Selector.X + 12, Selector.Y + 13);
        _surface.FillRoundRect(Submit, 6, Theme.Accent);
        _surface.DrawTextCentered(_login.Busy ? "Signing in..." : "Sign in", Fonts.SemiBold, 16, Theme.WindowBackground, Submit, true);
        _surface.DrawText(Accounts.Error is not null ? "Accounts unavailable. Use serial rescue." : _login.Message, Fonts.Regular, 12, Theme.TextSecondary, Card.X + 28, Card.Y + 415);
        foreach (var button in new[] { (Restart, "Restart"), (Shutdown, "Shut down") })
        {
            _surface.FillRoundRect(button.Item1, 6, Theme.Panel);
            _surface.DrawTextCentered(button.Item2, Fonts.Regular, 13, Theme.TextPrimary, button.Item1, true);
        }
        Cursor.Draw(_surface, _input.X, _input.Y);
        var screen = SessionDisplay.Screen; screen.DrawArray(_surface.Pixels, 0, 0, _surface.Width, _surface.Height); screen.Display();
    }
}
