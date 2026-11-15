using System;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell.Sunset;

/// <summary>The lock screen: the wallpaper, the time and a password field for the signed-in person, drawn
/// opaque over everything. While it is up no window, taskbar or portal dialog is drawn and no input reaches
/// them; programs keep running. Only the account that is signed in can unlock it, with its own password.</summary>
internal sealed class LockScreen
{
    private readonly StringBuilder _password = new();
    private readonly LoginPrompt _login = new((name, password) => Accounts.Service?.Authenticate(name, password));
    private readonly ScreenLockPolicy _policy = new();
    private string _message = "";
    private string _name = "";
    private uint _uid;
    private long _minute = -1, _wait = -1;
    private Rect _field, _unlock;

    public bool IsLocked { get; private set; }

    // The card blurs the dimmed wallpaper behind it (only the wallpaper is ever behind the lock screen).
    private readonly HostMaterial _material = new(400 * 280);
    public void Dispose() => _material.Dispose();

    public void Lock(string name, uint uid)
    {
        if (IsLocked) { return; }
        _name = name; _uid = uid; _password.Clear(); _message = ""; IsLocked = true;
        Zenith.Core.Log.Write("session", "locked (" + name + ")");
    }

    private static long Now => System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>An answer from the account service, the clock's minute or the wait changing; true when the
    /// screen needs drawing again.</summary>
    public bool Tick()
    {
        if (!IsLocked) { return false; }
        bool changed = false;
        if (_login.TakeResult(out var identity))
        {
            changed = true;
            if (identity is not null && identity.Uid == _uid)
            {
                _policy.Succeeded(); _password.Clear(); _message = ""; IsLocked = false;
                Zenith.Core.Log.Write("session", "unlocked");
                return true;
            }
            _policy.Failed(Now);
            _message = "Incorrect password. Try again.";
            Zenith.Core.Log.Write("session", "unlock failed (" + _policy.Failures + ")");
        }
        long minute = Zenith.Core.Time.SystemClock.Now.Ticks / TimeSpan.TicksPerMinute, wait = _policy.Wait(Now);
        if (minute != _minute || wait != _wait) { _minute = minute; _wait = wait; changed = true; }
        return changed;
    }

    public void Key(KeyEvent key)
    {
        if (_login.Busy) { return; }
        switch (key.Key)
        {
            case ConsoleKeyEx.Enter: Submit(); return;
            case ConsoleKeyEx.Escape: _password.Clear(); _message = ""; return;
            case ConsoleKeyEx.Backspace:
                if (_password.Length > 0) { if ((key.Modifiers & ConsoleModifiers.Control) != 0) { _password.Clear(); } else { _password.Length--; } }
                return;
        }
        if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == ConsoleModifiers.Control) { return; }
        if (key.KeyChar >= ' ' && !char.IsControl(key.KeyChar) && _password.Length < 256) { _password.Append(key.KeyChar); _message = ""; }
    }

    public void Click(int x, int y) { if (_unlock.Contains(x, y)) { Submit(); } }

    public long HoverKey(int x, int y) => _unlock.Contains(x, y) ? 2 : 1;

    private void Submit()
    {
        if (_login.Busy || _password.Length == 0) { return; }
        long wait = _policy.Wait(Now);
        if (wait > 0) { _message = "Too many attempts. Try again in " + wait + " s."; return; }
        string password = _password.ToString(); _password.Clear();
        _login.Submit(_name, password);
    }

    public void Draw(Surface s, Surface wallpaper, int mouseX, int mouseY)
    {
        s.CopyFrom(wallpaper);
        s.FillRect(s.Bounds, 0xB0000000U);
        DateTime now = Zenith.Core.Time.SystemClock.Now;
        string time = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string date = now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.InvariantCulture);
        int top = Math.Max(24, s.Height / 2 - 230);
        s.DrawTextCentered(time, Fonts.SemiBold, 72, 0xFFF1F2F4U, new Rect(0, top, s.Width, 90), true);
        s.DrawTextCentered(date, Fonts.Regular, 18, 0xFFC9CCD3U, new Rect(0, top + 92, s.Width, 28), true);

        int w = Math.Min(360, s.Width - 32), h = 252;
        Rect card = new((s.Width - w) / 2, top + 150, w, h);
        _material.Capture(s, card.Intersect(s.Bounds), Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
        s.DrawShadow(card, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
        _material.Paint(s, card.Intersect(s.Bounds), Theme.WindowBackground | 0xff000000U);
        s.StrokeRoundRect(card, Theme.WindowRadius, Theme.WindowBorder);
        Rect avatar = new(card.X + (w - 56) / 2, card.Y + 22, 56, 56);
        s.FillRoundRect(avatar, 28, Theme.AccentSoft);
        if (!Icons.Draw(s, "lock", avatar.X + 16, avatar.Y + 16, 24, Theme.Accent))
        { s.DrawTextCentered(_name.Length > 0 ? char.ToUpperInvariant(_name[0]).ToString() : "?", Fonts.SemiBold, 20, Theme.Accent, avatar, true); }
        s.DrawTextCentered(_name, Fonts.SemiBold, 16, Theme.TextPrimary, new Rect(card.X, avatar.Bottom + 10, w, 22), true);
        s.DrawTextCentered("Locked", Fonts.Regular, 12, Theme.TextSecondary, new Rect(card.X, avatar.Bottom + 32, w, 18), true);
        _field = new Rect(card.X + 24, avatar.Bottom + 60, w - 48, AppStyle.ControlHeight);
        string dots = new('*', Math.Min(_password.Length, 32));
        AppStyle.Field(s, _field, _password.Length == 0 && !_login.Busy ? "" : dots, true);
        if (_password.Length == 0 && !_login.Busy)
        { s.DrawText("Password", Fonts.Regular, Theme.TextBody, Theme.TextMuted, _field.X + 12, _field.Y + (_field.H - Theme.TextBody) / 2 - 2); }
        if (!_login.Busy)
        {
            int caret = _field.X + 12 + Fonts.Regular.MeasureString(dots, Theme.TextBody);
            s.FillRect(new Rect(Math.Min(caret, _field.Right - 10), _field.Y + 9, 2, _field.H - 18), Theme.Accent);
        }
        _unlock = new Rect(_field.X, _field.Bottom + 10, _field.W, AppStyle.ControlHeight);
        long wait = _policy.Wait(Now);
        AppStyle.Button(s, _unlock, _login.Busy ? "Checking..." : "Unlock", primary: true, enabled: !_login.Busy && wait == 0);
        if (!_login.Busy && wait == 0 && _unlock.Contains(mouseX, mouseY)) { s.FillRoundRect(_unlock, Theme.ControlRadius, Theme.Hover); }
        string status = wait > 0 ? "Too many attempts. Try again in " + wait + " s." : _message;
        if (status.Length > 0)
        { s.DrawTextCentered(status, Fonts.Regular, 12, Theme.Danger, new Rect(card.X, _unlock.Bottom + 6, w, 18), true); }
        s.DrawTextCentered("Enter unlocks · programs keep running", Fonts.Regular, 12, 0xFFA0A4AEU, new Rect(0, s.Height - 40, s.Width, 20), true);
    }
}
