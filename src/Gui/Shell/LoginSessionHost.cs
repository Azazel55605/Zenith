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
    private Surface _surface = null!;   // everything but the cursor
    private Surface _frame = null!;     // surface + cursor, what is on screen
    private Surface _background = null!;
    private string? _confirm;           // the power action waiting for a second click
    private int _hover, _cursorX, _cursorY;
    private bool _started;
    private string _title = "Zenith", _subtitle = "Sign in to your workspace";
    private bool _dirty = true;
    public LoginSessionHost(SessionMode selected) => _mode = selected;
    public bool CanStop => !_login.Busy;
    public string? StopBlocker(CommandShell? requester) => _login.Busy ? "Authentication is running." : null;
    public void Stop()
    {
        SessionDisplay.LiveFrame = null;
        if (_started) { SessionDisplay.KeepFrame(_frame.Pixels); }
        _name.Clear(); _password.Clear(); _surface = null!; _frame = null!; _background = null!;
    }
    public void BeginStop() { }
    public void Start()
    {
        NativeAppearance.Reload();
        var screen = SessionDisplay.Screen; _surface = new Surface(screen.Width, screen.Height); _frame = new Surface(screen.Width, screen.Height);
        SessionDisplay.LiveFrame = _frame.Pixels;
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
    }
    private const int CardWidth = 420, CardHeight = 396, Padding = 28, PowerWidth = 120, ConfirmWidth = 440, ConfirmHeight = 52;
    private Rect Card
    {
        get
        {
            int width = Math.Min(CardWidth, _surface.Width - 32);
            return new((_surface.Width - width) / 2, Math.Max(8, (_surface.Height - CardHeight) / 2 - 16), width, CardHeight);
        }
    }
    private Rect Field(int index) => new(Card.X + Padding, Card.Y + 116 + index * 64, Card.W - 2 * Padding, AppStyle.ControlHeight);
    private Rect Segment(int index)
    {
        int width = (Card.W - 2 * Padding - 8) / 2;
        return new(Card.X + Padding + index * (width + 8), Card.Y + 244, width, AppStyle.ControlHeight);
    }
    private Rect Submit => new(Card.X + Padding, Card.Y + 300, Card.W - 2 * Padding, AppStyle.ControlHeight);
    private Rect Restart => new(_surface.Width / 2 - PowerWidth - 10, _surface.Height - 56, PowerWidth, AppStyle.ControlHeight);
    private Rect Shutdown => new(_surface.Width / 2 + 10, _surface.Height - 56, PowerWidth, AppStyle.ControlHeight);
    private Rect ConfirmPanel => new((_surface.Width - ConfirmWidth) / 2, _surface.Height - 66, ConfirmWidth, ConfirmHeight);
    private Rect ConfirmYes => new(ConfirmPanel.Right - 8 - 100, ConfirmPanel.Y + 8, 100, AppStyle.ControlHeight);
    private Rect ConfirmNo => new(ConfirmYes.X - 8 - 84, ConfirmPanel.Y + 8, 84, AppStyle.ControlHeight);
    /// <summary>Names the control under the pointer; a change means its hover state needs drawing.</summary>
    private int HoverAt(int x, int y)
    {
        if (_login.Busy) { return 0; }
        if (_confirm is not null) { return ConfirmYes.Contains(x, y) ? 1 : ConfirmNo.Contains(x, y) ? 2 : 0; }
        return Segment(0).Contains(x, y) ? 3 : Segment(1).Contains(x, y) ? 4 : Submit.Contains(x, y) ? 5
            : Restart.Contains(x, y) ? 6 : Shutdown.Contains(x, y) ? 7 : 0;
    }
    public void Tick()
    {
        if (_login.TakeResult(out var identity))
        {
            _dirty = true;
            if (identity is not null) { SessionRuntime.Activate(identity, _mode); return; }
            Log.Write("session", "authentication failed");
        }
        _input.Poll();
        int hover = HoverAt(_input.X, _input.Y);
        bool cursorOnly = false;
        if (_input.Moved)
        {
            if (hover != _hover) { _dirty = true; }
            else { cursorOnly = true; }
        }
        _hover = hover;
        if (!_login.Busy && _confirm is not null)
        {
            HandleConfirm();
            if (_confirm is null) { _dirty = true; }
        }
        else if (!_login.Busy)
        {
            if (_input.LeftPressed)
            {
                if (Restart.Contains(_input.X, _input.Y)) { AskConfirm("Restart"); }
                else if (Shutdown.Contains(_input.X, _input.Y)) { AskConfirm("Shut down"); }
                else if (Field(0).Contains(_input.X, _input.Y)) { _focus = 0; }
                else if (Field(1).Contains(_input.X, _input.Y)) { _focus = 1; }
                else if (Segment(0).Contains(_input.X, _input.Y)) { _focus = 2; Choose(SessionMode.Desktop); }
                else if (Segment(1).Contains(_input.X, _input.Y)) { _focus = 2; Choose(SessionMode.Headless); }
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
        if (!_started)
        {
            Draw(); _dirty = false; _started = true;
            Log.Write("session", "login ready; selection=" + SessionModes.Name(_mode));
            return;
        }
        if (_dirty) { Draw(); _dirty = false; }
        else if (cursorOnly) { DrawCursorOnly(); }
    }
    private void AskConfirm(string action)
    {
        _confirm = action;
        Log.Write("session", "power confirmation: " + action);
    }
    /// <summary>A power action asks first; only Escape, Cancel or a second, deliberate click respond, so typing cannot trigger it.</summary>
    private void HandleConfirm()
    {
        foreach (KeyEvent key in _input.Keys)
        {
            if (key.Key == ConsoleKeyEx.Escape) { _confirm = null; Log.Write("session", "power confirmation cancelled"); return; }
        }
        if (!_input.LeftPressed) { return; }
        bool run = ConfirmYes.Contains(_input.X, _input.Y);
        string action = _confirm!;
        _confirm = null;
        if (!run) { Log.Write("session", "power confirmation cancelled"); return; }
        using var root = CredentialContext.Enter(Credentials.Root);
        try { if (action == "Restart") { Zenith.Core.PowerControl.Reboot(); } else { Zenith.Core.PowerControl.PowerOff(); } }
        catch (Exception e) { Zenith.Core.Log.Write("session", "power action failed: " + e.Message); }
    }
    private void Toggle() => Choose(_mode == SessionMode.Desktop ? SessionMode.Headless : SessionMode.Desktop);
    private void Choose(SessionMode next)
    {
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
        _surface.DrawShadow(Card, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
        _surface.FillRoundRect(Card, Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
        _surface.StrokeRoundRect(Card, Theme.WindowRadius, Theme.WindowBorder);
        Logo.Draw(_surface, new Rect(Card.X + Padding, Card.Y + 28, 32, 32));
        _surface.DrawText(_title, Fonts.SemiBold, Theme.TextHeading, Theme.TextPrimary, Card.X + Padding + 42, Card.Y + 28);
        _surface.DrawText(_subtitle, Fonts.Regular, Theme.TextBody, Theme.TextSecondary, Card.X + Padding, Card.Y + 68);
        for (int i = 0; i < 2; i++)
        {
            Rect field = Field(i);
            string text = i == 0 ? _name.ToString() : new string('*', Math.Min(_password.Length, 32));
            Label(i == 0 ? "Username" : "Password", field);
            AppStyle.Field(_surface, field, text, _focus == i);
            if (_focus == i && !_login.Busy)
            {
                int caret = field.X + 12 + Fonts.Regular.MeasureString(text, Theme.TextBody);
                _surface.FillRect(new Rect(Math.Min(caret, field.Right - 10), field.Y + 9, 2, field.H - 18), Theme.Accent);
            }
        }
        Label("Session", Segment(0));
        DrawSegment(0, "Desktop", SessionMode.Desktop);
        DrawSegment(1, "Headless", SessionMode.Headless);
        AppStyle.Button(_surface, Submit, _login.Busy ? "Signing in..." : "Sign in", primary: true, enabled: !_login.Busy);
        Hovered(Submit, 5);
        bool failed = Accounts.Error is not null || _login.Message.StartsWith("Authentication failed", StringComparison.Ordinal);
        _surface.DrawText(Accounts.Error is not null ? "Accounts unavailable. Use serial rescue." : _login.Message, Fonts.Regular, Theme.TextSmall,
            failed ? Theme.Danger : Theme.TextSecondary, Card.X + Padding, Card.Y + 352);
        if (_confirm is string action)
        {
            _surface.DrawShadow(ConfirmPanel, Theme.WindowRadius, 14, 70, 3);
            _surface.FillRoundRect(ConfirmPanel, Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
            _surface.StrokeRoundRect(ConfirmPanel, Theme.WindowRadius, Theme.WindowBorder);
            _surface.DrawTextCentered(action + " this computer?", Fonts.Regular, Theme.TextBody, Theme.TextPrimary,
                new Rect(ConfirmPanel.X + 16, ConfirmPanel.Y, ConfirmNo.X - ConfirmPanel.X - 24, ConfirmPanel.H), false);
            AppStyle.Button(_surface, ConfirmNo, "Cancel");
            Hovered(ConfirmNo, 2);
            AppStyle.Button(_surface, ConfirmYes, action, danger: true);
            Hovered(ConfirmYes, 1);
        }
        else
        {
            AppStyle.Button(_surface, Restart, "Restart");
            Hovered(Restart, 6);
            AppStyle.Button(_surface, Shutdown, "Shut down");
            Hovered(Shutdown, 7);
        }
        _frame.CopyFrom(_surface);
        _cursorX = _input.X; _cursorY = _input.Y;
        Cursor.Draw(_frame, _cursorX, _cursorY);
        if (SessionDisplay.FadePending) { SessionDisplay.FadeTo(_frame.Pixels, _frame.Width, _frame.Height); }
        else { Present(); }
    }
    private void Label(string text, Rect control) => _surface.DrawText(text, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, control.X, control.Y - 20);
    private void Hovered(Rect control, int key)
    {
        if (_hover == key) { _surface.FillRoundRect(control, Theme.ControlRadius, Theme.Hover); }
    }
    private void DrawSegment(int index, string label, SessionMode mode)
    {
        Rect box = Segment(index);
        bool selected = _mode == mode, available = SessionRuntime.Available(mode);
        _surface.FillRoundRect(box, Theme.ControlRadius, selected ? Theme.AccentSoft : Theme.Raised);
        _surface.StrokeRoundRect(box, Theme.ControlRadius, selected ? Theme.Accent : Theme.WindowBorder);
        if (selected && _focus == 2) { _surface.StrokeRoundRect(box.Inflate(2), Theme.ControlRadius + 2, Theme.Accent); }
        if (available) { Hovered(box, 3 + index); }
        _surface.DrawTextCentered(label, selected ? Fonts.SemiBold : Fonts.Regular, Theme.TextBody, available ? Theme.TextPrimary : Theme.TextMuted, box, true);
    }
    private void DrawCursorOnly()
    {
        _frame.CopyRect(_surface, Cursor.Bounds(_cursorX, _cursorY));
        _cursorX = _input.X; _cursorY = _input.Y;
        Cursor.Draw(_frame, _cursorX, _cursorY);
        Present();
    }
    private void Present()
    {
        var screen = SessionDisplay.Screen;
        screen.DrawArray(_frame.Pixels, 0, 0, _frame.Width, _frame.Height);
        screen.Display();
    }
}
