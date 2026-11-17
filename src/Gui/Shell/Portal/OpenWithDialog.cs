using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Portal;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell.Portal;

/// <summary>org.zenith.portal.OpenWith: opens a file with the person's remembered app, else with the app that
/// declares its type, without showing anything; otherwise (and for "Open with...") lists the apps that take
/// files, with "Always use this app for .EXT files". The desktop starts the app, never the program asking.</summary>
internal sealed class OpenWithDialog : IPortalDialog
{
    private const int Row = 40, Visible = 6;
    /// <summary>Starts an app with a file (the desktop's OpenAppWith); set when the desktop starts.</summary>
    public static Func<string, string, bool>? Launch;

    private readonly PortalRequest _request;
    private readonly OpenWithPrompt _prompt;
    private readonly List<AppInfo> _apps;
    private readonly int _declared;
    private int _selected, _first;
    private bool _always;
    private Rect _list, _check, _cancel, _open;

    public OpenWithDialog(PortalRequest request)
    {
        _request = request;
        _prompt = request.Prompt as OpenWithPrompt ?? throw new ArgumentException("open-with request without a prompt");
        _apps = AppRegistry.OpenersFor(_prompt.Extension);
        foreach (var app in _apps) { if (_prompt.Extension.Length > 0 && Array.IndexOf(app.Opens, _prompt.Extension) >= 0) { _declared++; } }
        if (_apps.Count == 0) { Done = true; Portals.OpenWith.Declined(request, "no installed app opens files"); return; }
        int remembered = _prompt.Remembered is null ? -1 : _apps.FindIndex(a => a.Name == _prompt.Remembered);
        _selected = remembered >= 0 ? remembered : 0;
        // Open: no question when the app is known.
        if (!_prompt.Choose && (remembered >= 0 || _declared > 0)) { Start(_apps[_selected], false); }
    }

    public string Heading => "Open with";
    public bool Done { get; private set; }
    private string FileName => _prompt.Path[(_prompt.Path.LastIndexOf('/') + 1)..];

    public int Height(int width) => 22 + 14 + Math.Min(_apps.Count, Visible) * Row + 12 + (_prompt.Extension.Length > 0 ? 34 : 0) + 12 + AppStyle.ControlHeight;

    public void Draw(Surface surface, Rect body, int mouseX, int mouseY)
    {
        int y = body.Y;
        surface.DrawText(PortalHost.Fit("Choose an app for \"" + FileName + "\".", body.W, Theme.TextBody), Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, body.X, y);
        y += 22 + 14;
        _list = new Rect(body.X, y, body.W, Math.Min(_apps.Count, Visible) * Row);
        for (int i = _first; i < _apps.Count && i < _first + Visible; i++)
        {
            var row = new Rect(body.X, y + (i - _first) * Row, body.W, Row - 4);
            bool on = i == _selected;
            if (on) { surface.FillRoundRect(row, Theme.ControlRadius, Theme.AccentSoft); }
            else if (row.Contains(mouseX, mouseY)) { surface.FillRoundRect(row, Theme.ControlRadius, Theme.Hover); }
            var app = _apps[i];
            if (!Icons.Draw(surface, app.Icon ?? AppRegistry.IconFor(app.Name), row.X + 10, row.Y + (row.H - 20) / 2, 20, on ? Theme.Accent : Theme.TextPrimary))
            { surface.DrawTextCentered(app.DisplayName[..1], Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, new Rect(row.X + 10, row.Y, 20, row.H), true); }
            surface.DrawText(app.DisplayName, on ? Fonts.SemiBold : Fonts.Regular, Theme.TextBody, Theme.TextPrimary, row.X + 42, row.Y + (row.H - 18) / 2);
            if (i < _declared) { string note = "Opens ." + _prompt.Extension + " files"; surface.DrawText(note, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, row.Right - 12 - Fonts.Regular.MeasureString(note, Theme.TextSmall), row.Y + (row.H - 14) / 2); }
        }
        y += _list.H + 12;
        if (_prompt.Extension.Length > 0)
        {
            _check = new Rect(body.X, y, body.W, 30);
            AppStyle.Checkbox(surface, _check, "Always use this app for ." + _prompt.Extension + " files", _always);
            y += 34;
        }
        else { _check = default; }
        y += 12;
        _open = new Rect(body.Right - 120, y, 120, AppStyle.ControlHeight);
        _cancel = new Rect(_open.X - 8 - 100, y, 100, AppStyle.ControlHeight);
        AppStyle.Button(surface, _cancel, "Cancel");
        AppStyle.Button(surface, _open, "Open", primary: true);
        if (_cancel.Contains(mouseX, mouseY)) { surface.FillRoundRect(_cancel, Theme.ControlRadius, Theme.Hover); }
        if (_open.Contains(mouseX, mouseY)) { surface.FillRoundRect(_open, Theme.ControlRadius, Theme.Hover); }
    }

    public void Key(KeyEvent key)
    {
        if (Done) { return; }
        switch (key.Key)
        {
            case ConsoleKeyEx.Escape: Cancel(); return;
            case ConsoleKeyEx.Enter: Start(_apps[_selected], _always); return;
            case ConsoleKeyEx.UpArrow: Select(_selected - 1); return;
            case ConsoleKeyEx.DownArrow: Select(_selected + 1); return;
            case ConsoleKeyEx.Spacebar: if (_prompt.Extension.Length > 0) { _always = !_always; } return;
        }
    }
    private void Select(int index)
    {
        _selected = Math.Clamp(index, 0, _apps.Count - 1);
        if (_selected < _first) { _first = _selected; }
        if (_selected >= _first + Visible) { _first = _selected - Visible + 1; }
    }

    public void Click(int x, int y)
    {
        if (Done) { return; }
        if (_list.Contains(x, y)) { int i = _first + (y - _list.Y) / Row; if (i < _apps.Count) { if (i == _selected && _lastClick == i) { Start(_apps[i], _always); } _selected = i; _lastClick = i; } return; }
        _lastClick = -1;
        if (_check.W > 0 && _check.Contains(x, y)) { _always = !_always; }
        else if (_cancel.Contains(x, y)) { Cancel(); }
        else if (_open.Contains(x, y)) { Start(_apps[_selected], _always); }
    }
    private int _lastClick = -1;

    public long HoverKey(int x, int y) => _cancel.Contains(x, y) ? 1 : _open.Contains(x, y) ? 2 : _list.Contains(x, y) ? 3 + (y - _list.Y) / Row : 0;
    public bool Tick() => false;

    private void Start(AppInfo app, bool always)
    {
        Done = true;
        Zenith.Core.Log.Write("portal", "open with " + app.Name + ": " + _prompt.Path + " for " + _request.Caller.Program + " (pid " + _request.Caller.Pid + ")");
        if (Launch?.Invoke(app.Name, _prompt.Path) == true) { Portals.OpenWith.Opened(_request, app.Name, always); }
        else { Portals.OpenWith.Failed(_request, app.DisplayName + " could not be started"); }
    }
    private void Cancel() { Done = true; Portals.OpenWith.Declined(_request, "dismissed"); }
}
