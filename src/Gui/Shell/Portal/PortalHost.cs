using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Portal;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell.Portal;

/// <summary>The body of a portal dialog for one interface. The host draws the trusted frame around
/// it (dimmed screen, card, heading, which program asks) and routes input to it while it is open.</summary>
internal interface IPortalDialog
{
    string Heading { get; }
    /// <summary>Height of the body at this width.</summary>
    int Height(int width);
    void Draw(Surface surface, Rect body, int mouseX, int mouseY);
    void Key(KeyEvent key);
    void Click(int x, int y);
    /// <summary>Background work (authentication finishing); true when it needs drawing again.</summary>
    bool Tick();
    /// <summary>The dialog answered its request (through the broker) and can close.</summary>
    bool Done { get; }
    long HoverKey(int x, int y);
}

/// <summary>Which interfaces the desktop can show, and how. A new portal interface adds its dialog here.</summary>
internal static class PortalDialogs
{
    private static readonly Dictionary<string, Func<PortalRequest, IPortalDialog>> s_dialogs = new(StringComparer.Ordinal)
    {
        [AuthorizationPortal.InterfaceName] = request => new AuthorizationDialog(request),
    };
    public static void Register(string name, Func<PortalRequest, IPortalDialog> create) => s_dialogs[name] = create;
    public static bool Supports(string name) => s_dialogs.ContainsKey(name);
    public static IPortalDialog? Create(PortalRequest request) => s_dialogs.TryGetValue(request.Interface.Name, out var create) ? create(request) : null;
}

/// <summary>The desktop's portal frontend: one dialog at a time, above everything (windows, the
/// taskbar, Prism), holding the keyboard and the pointer until it is answered. Programs cannot draw
/// over it or send it input, so what it shows and what is typed into it stay the desktop's.</summary>
internal sealed class PortalHost
{
    private const int CardWidth = 460, Pad = 24, Head = 92;
    private IPortalDialog? _dialog;
    private PortalRequest? _request;
    private Rect _card;

    public bool IsOpen => _dialog is not null;

    public void Attach() => PortalBroker.Attach(PortalDialogs.Supports);
    public void Detach() { PortalBroker.Detach(); _dialog = null; _request = null; }

    /// <summary>Shows the next request, notices answers and withdrawals; true when the screen changes.</summary>
    public bool Tick()
    {
        if (_dialog is not null)
        {
            bool changed = _dialog.Tick();
            if (_dialog.Done || _request!.Finished)
            {
                Zenith.Core.Log.Write("portal", "dialog closed: " + _request!.State.ToString().ToLowerInvariant());
                _dialog = null; _request = null; return true;
            }
            return changed;
        }
        var next = PortalBroker.TakeNext();
        if (next is null) { return false; }
        var dialog = PortalDialogs.Create(next);
        if (dialog is null) { PortalBroker.Finish(next, PortalState.Unavailable, "the desktop cannot show this request"); return false; }
        _request = next; _dialog = dialog;
        Zenith.Core.Log.Write("portal", "dialog shown: " + next.Interface.Name + "." + next.Method + " for " + next.Caller.Program + " (pid " + next.Caller.Pid + ")");
        return true;
    }

    public void HandleInput(Input input)
    {
        if (_dialog is null) { return; }
        foreach (KeyEvent key in input.Keys) { if (_dialog is null) { break; } _dialog.Key(key); }
        if (_dialog is not null && input.LeftPressed) { _dialog.Click(input.X, input.Y); }
    }

    public long HoverKey(int x, int y) => _dialog is null ? 0 : 1 + _dialog.HoverKey(x, y);

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        if (_dialog is null || _request is null) { return; }
        surface.FillRect(surface.Bounds, 0x99000000U);   // dim everything below: the dialog is the only thing to answer
        int width = Math.Min(CardWidth, surface.Width - 32), inner = width - 2 * Pad;
        int height = Head + _dialog.Height(inner) + Pad;
        _card = new Rect((surface.Width - width) / 2, Math.Max(16, (surface.Height - height) / 2 - 24), width, height);
        surface.DrawShadow(_card, Theme.WindowRadius, Theme.ShadowSize, Theme.ShadowAlpha, 6);
        surface.FillRoundRect(_card, Theme.WindowRadius, Theme.WindowBackground | 0xff000000U);
        surface.StrokeRoundRect(_card, Theme.WindowRadius, Theme.WindowBorder);
        // The heading names who asks; the badge says the desktop draws this, not the program.
        Rect badge = new(_card.X + Pad, _card.Y + Pad, 36, 36);
        surface.FillRoundRect(badge, 10, Theme.AccentSoft);
        if (!Icons.Draw(surface, "shield", badge.X + 6, badge.Y + 6, 24, Theme.Accent)) { Logo.Draw(surface, new Rect(badge.X + 6, badge.Y + 6, 24, 24)); }
        surface.DrawText(_dialog.Heading, Fonts.SemiBold, Theme.TextHeading, Theme.TextPrimary, badge.Right + 12, _card.Y + Pad - 2);
        surface.DrawText(Fit("Requested by " + _request.Caller.Program + " (process " + _request.Caller.Pid + ")", inner - 48, Theme.TextSmall),
            Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, badge.Right + 12, _card.Y + Pad + 24);
        surface.FillRect(new Rect(_card.X + Pad, _card.Y + Head - 16, inner, 1), Theme.TitleDivider);
        _dialog.Draw(surface, new Rect(_card.X + Pad, _card.Y + Head, inner, height - Head - Pad), mouseX, mouseY);
    }

    internal static string Fit(string text, int width, int size)
    {
        if (Fonts.Regular.MeasureString(text, size) <= width) { return text; }
        while (text.Length > 1 && Fonts.Regular.MeasureString(text + "...", size) > width) { text = text[..^1]; }
        return text + "...";
    }

    /// <summary>Breaks text into lines no wider than <paramref name="width"/>.</summary>
    internal static List<string> Wrap(string text, FontFace font, int size, int width)
    {
        var lines = new List<string>(); string line = "";
        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureString(next, size) > width) { lines.Add(line); line = word; }
            else { line = next; }
        }
        if (line.Length > 0) { lines.Add(line); }
        return lines;
    }
}
