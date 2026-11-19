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
    public bool FramePending => false;

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
    // Login styles (docs/design/startup-concept.svg, kernel 0.0.71): 0 Zenith card, 1 Accounts, 2 Clock, 3 Split.
    // Accounts, Clock and Split preselect the last account that signed in here; Clock waits for a key or click.
    private int _style;
    private System.Collections.Generic.List<string> _accounts = new();
    private int _account = -1;
    private bool _revealed = true;
    private long _clockMinute = -1;
    private const string LastPath = "/var/lib/zenith/last-login";
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
        _style = (int)Math.Min(3u, Zenith.Core.Configuration.SystemConfiguration.Settings?.Style(14) ?? 0);
        if (_style > 0)
        {
            try { _accounts = Accounts.Service?.SignInAccounts() ?? new(); } catch (Exception) { _accounts = new(); }
            // No account to offer (a fresh system): the card is the only way in.
            if (_accounts.Count == 0) { _style = 0; }
            else
            {
                string? last = null;
                try { last = Zenith.Core.Storage.BoundedFile.ReadText(LastPath, 64)?.Trim(); } catch (Exception) { }
                _account = Math.Max(0, last is null ? -1 : _accounts.IndexOf(last));
                if (_accounts.Count > 1 && _account == 0 && _accounts[0] == "root" && last is null) { _account = 1; }
                _name.Clear(); _name.Append(_accounts[_account]); _focus = 1;
                _revealed = _style != 2;
            }
        }
        Log.Write("session", "login style " + new[] { "card", "accounts", "clock", "split" }[_style] + " accounts=" + _accounts.Count);
        SessionDisplay.ConfigurePointer();
    }
    private void SelectAccount(int index)
    {
        if (index < 0 || index >= _accounts.Count) { return; }
        _account = index; _name.Clear(); _name.Append(_accounts[index]); _password.Clear(); _focus = 1; _dirty = true;
    }
    /// <summary>Account tiles (Accounts) or rows (Split).</summary>
    private Rect AccountTile(int i)
    {
        if (_style == 3) { return new Rect(Card.X + 16, Card.Y + 64 + i * 48, Card.W - 32, 44); }
        int n = _accounts.Count, w = 112, gap = 12, total = n * w + (n - 1) * gap;
        return new Rect((_surface.Width - total) / 2 + i * (w + gap), Card.Y - 148, w, 128);
    }
    private int Shift => _style == 3 ? 56 + _accounts.Count * 48 : 0;
    private const int CardWidth = 420, CardHeight = 396, Padding = 28, PowerWidth = 120, ConfirmWidth = 440, ConfirmHeight = 52;
    private Rect Card
    {
        get
        {
            if (_style == 3) { int panel = Math.Min(380, _surface.Width / 2); return new(_surface.Width - panel, 0, panel, _surface.Height); }
            int width = Math.Min(CardWidth, _surface.Width - 32);
            int y = Math.Max(8, (_surface.Height - CardHeight) / 2 - 16);
            if (_style == 1) { y = Math.Min(Math.Max(170, y + 70), Math.Max(8, _surface.Height - CardHeight - 72)); }
            return new((_surface.Width - width) / 2, y, width, CardHeight);
        }
    }
    private Rect Field(int index) => new(Card.X + Padding, Card.Y + 116 + Shift + index * 64, Card.W - 2 * Padding, AppStyle.ControlHeight);
    private Rect Segment(int index)
    {
        int width = (Card.W - 2 * Padding - 8) / 2;
        return new(Card.X + Padding + index * (width + 8), Card.Y + 244 + Shift, width, AppStyle.ControlHeight);
    }
    private Rect Submit => new(Card.X + Padding, Card.Y + 300 + Shift, Card.W - 2 * Padding, AppStyle.ControlHeight);
    private int PowerCenter => _style == 3 ? (_surface.Width - Card.W) / 2 : _surface.Width / 2;
    private Rect Restart => new(PowerCenter - PowerWidth - 10, _surface.Height - 56, PowerWidth, AppStyle.ControlHeight);
    private Rect Shutdown => new(PowerCenter + 10, _surface.Height - 56, PowerWidth, AppStyle.ControlHeight);
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
            if (identity is not null)
            {
                try { var store = new Zenith.Core.Configuration.NativeSettingsStore(); store.Replace(LastPath, _name.ToString() + "\n"); } catch (Exception) { }
                SessionRuntime.Activate(identity, _mode); return;
            }
            Log.Write("session", "authentication failed");
            Zenith.Core.Audio.SystemSounds.Play("error");
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
        else if (!_login.Busy && !_revealed)
        {
            // Clock: any key or click brings up the card for the preselected account; that input is not typed.
            if (_input.LeftPressed && Restart.Contains(_input.X, _input.Y)) { AskConfirm("Restart"); }
            else if (_input.LeftPressed && Shutdown.Contains(_input.X, _input.Y)) { AskConfirm("Shut down"); }
            else if (_input.LeftPressed || _input.Keys.Count > 0) { _revealed = true; Log.Write("session", "login clock revealed"); }
            if (_input.LeftPressed || _input.Keys.Count > 0) { _dirty = true; }
        }
        else if (!_login.Busy)
        {
            if (_input.LeftPressed && (_style == 1 || _style == 3))
            {
                for (int i = 0; i < _accounts.Count; i++) { if (AccountTile(i).Contains(_input.X, _input.Y)) { SelectAccount(i); } }
            }
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
                // Accounts and Split: Left/Right (Up/Down in Split) choose another account while the password is empty.
                if (_style is 1 or 3 && _focus == 1 && _password.Length == 0 && key.Key is ConsoleKeyEx.LeftArrow or ConsoleKeyEx.RightArrow or ConsoleKeyEx.UpArrow or ConsoleKeyEx.DownArrow)
                { SelectAccount(_account + (key.Key is ConsoleKeyEx.LeftArrow or ConsoleKeyEx.UpArrow ? -1 : 1)); continue; }
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
        long minute = Zenith.Core.Time.SystemClock.Now.Ticks / TimeSpan.TicksPerMinute;
        if (_style is 2 or 3 && minute != _clockMinute) { _clockMinute = minute; _dirty = true; }
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
    private void DrawClock(int x, int y, int size, bool centered)
    {
        DateTime now = Zenith.Core.Time.SystemClock.Now;
        string time = now.Hour.ToString("00") + ":" + now.Minute.ToString("00");
        string[] days = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        string[] months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        string date = days[(int)now.DayOfWeek] + " " + now.Day + " " + months[now.Month - 1];
        if (centered)
        {
            _surface.DrawTextCentered(time, Fonts.Regular, size, 0xFFF1F2F4U, new Rect(0, y, _surface.Width, size + 8), true);
            _surface.DrawTextCentered(date, Fonts.Regular, size / 4, 0xFFD5D8DEU, new Rect(0, y + size + 12, _surface.Width, size / 4 + 8), true);
        }
        else
        {
            _surface.DrawText(time, Fonts.Regular, size, 0xFFF1F2F4U, x, y);
            _surface.DrawText(date, Fonts.Regular, size / 3, 0xFFD5D8DEU, x + 2, y + size + 10);
        }
    }
    private void DrawAccountTiles()
    {
        for (int i = 0; i < _accounts.Count; i++)
        {
            Rect tile = AccountTile(i); bool selected = i == _account;
            string name = _accounts[i], initial = name.Substring(0, 1).ToUpperInvariant();
            if (_style == 3)
            {
                if (selected) { _surface.FillRoundRect(tile, Theme.ControlRadius, Theme.AccentSoft); }
                _surface.FillRoundRect(new Rect(tile.X + 10, tile.Y + 8, 28, 28), 14, selected ? Theme.Accent : Theme.Raised);
                _surface.DrawTextCentered(initial, Fonts.SemiBold, Theme.TextBody, selected ? Theme.OnAccent : Theme.TextPrimary, new Rect(tile.X + 10, tile.Y + 8, 28, 28), true);
                _surface.DrawText(name, selected ? Fonts.SemiBold : Fonts.Regular, Theme.TextBody, Theme.TextPrimary, tile.X + 50, tile.Y + 12);
                continue;
            }
            if (selected)
            {
                _surface.FillRoundRect(tile, Theme.WindowRadius, Theme.AccentSoft);
                _surface.StrokeRoundRect(tile, Theme.WindowRadius, Theme.Accent);
            }
            Rect avatar = new(tile.X + (tile.W - 64) / 2, tile.Y + 12, 64, 64);
            _surface.FillRoundRect(avatar, 32, selected ? Theme.Accent : 0xFF5C606BU);
            _surface.DrawTextCentered(initial, Fonts.SemiBold, 26, selected ? Theme.OnAccent : 0xFFF1F2F4U, avatar, true);
            _surface.DrawTextCentered(name, selected ? Fonts.SemiBold : Fonts.Regular, Theme.TextBody, 0xFFF1F2F4U, new Rect(tile.X, tile.Y + 90, tile.W, 24), true);
        }
    }
    private void Draw()
    {
        _surface.CopyFrom(_background);
        if (!_revealed)
        {
            // Clock: the time over the wallpaper and the preselected account; any key brings up the card.
            _surface.FillRect(new Rect(0, 0, _surface.Width, _surface.Height), 0x33000000U);
            DrawClock(0, _surface.Height / 2 - 110, 96, true);
            Rect pill = new((_surface.Width - 260) / 2, _surface.Height - 140, 260, 40);
            _surface.FillRoundRect(pill, 20, 0xCC202125U);
            _surface.FillRoundRect(new Rect(pill.X + 10, pill.Y + 9, 22, 22), 11, Theme.Accent);
            _surface.DrawText(_accounts[_account] + " · press any key", Fonts.Regular, Theme.TextBody, 0xFFF1F2F4U, pill.X + 42, pill.Y + 11);
            DrawPower();
            FinishFrame();
            return;
        }
        if (_style == 3) { DrawClock(48, _surface.Height - 150, 64, false); }
        if (_style == 1) { DrawAccountTiles(); }
        _surface.DrawShadow(Card, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
        _surface.FillRoundRect(Card, Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
        _surface.StrokeRoundRect(Card, Theme.WindowRadius, Theme.WindowBorder);
        Logo.Draw(_surface, new Rect(Card.X + Padding, Card.Y + 28, 32, 32));
        _surface.DrawText(_title, Fonts.SemiBold, Theme.TextHeading, Theme.TextPrimary, Card.X + Padding + 42, Card.Y + 28);
        _surface.DrawText(_style == 3 ? "Choose an account" : _style == 1 ? "Password for " + _name : _subtitle, Fonts.Regular, Theme.TextBody, Theme.TextSecondary, Card.X + Padding, Card.Y + 68);
        if (_style == 3) { DrawAccountTiles(); }
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
        else { DrawPower(); }
        FinishFrame();
    }
    private void DrawPower()
    {
        if (_confirm is not null) { return; }
        AppStyle.Button(_surface, Restart, "Restart");
        Hovered(Restart, 6);
        AppStyle.Button(_surface, Shutdown, "Shut down");
        Hovered(Shutdown, 7);
    }
    private void FinishFrame()
    {
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
        FramePresenter.PresentFrame(screen, _frame.Pixels, _frame.Width, _frame.Height);
    }
}
