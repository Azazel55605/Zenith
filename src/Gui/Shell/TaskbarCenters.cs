using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using Zenith.Core.Audio;
using Zenith.Core.Network;
using Zenith.Core.Security;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>The taskbar's network and sound centers (2026-11-18): two buttons beside the clock, each opening a small
/// panel above the bar. Network: the state in words, address, the last minute of traffic, connect/disconnect (root
/// sessions) and the way into Beacon and Meridian. Sound: the output, mute, volume (also the wheel over the button) and
/// the programs playing. Both read the kernel services directly; changes go through the same rules as the native calls.</summary>
internal sealed class TaskbarCenters
{
    public enum Panel { None, Network, Sound }
    private const int PanelWidth = 320, Padding = 14;
    private readonly Func<string, string[]?, bool> _openApp;
    public TaskbarCenters(Func<string, string[]?, bool> openApp) { _openApp = openApp; }
    public Panel Open { get; set; }
    public Rect NetworkButton { get; private set; }
    public Rect SoundButton { get; private set; }
    private Rect _panel;
    private readonly List<(Rect Rect, int Action)> _actions = new();
    private Rect _slider;
    private string _note = "";
    private volatile bool _busy;
    private enum Act { Toggle = 1, Beacon, Diagnostics, NetworkSettings, Mute, Down, Up, SoundSettings }

    /// <summary>Lays the two buttons out to the left of x (the clock's left edge); returns their left edge.</summary>
    public int Layout(int right, int y, int size)
    {
        SoundButton = new Rect(right - size, y, size, size);
        NetworkButton = new Rect(right - 2 * size - 4, y, size, size);
        // Where they are, once per change: gates turn this into pointer moves.
        if (NetworkButton.X != _loggedX || NetworkButton.Y != _loggedY)
        {
            _loggedX = NetworkButton.X; _loggedY = NetworkButton.Y;
            Zenith.Core.Log.Write("taskbar", "centers network=" + (NetworkButton.X + size / 2) + "," + (NetworkButton.Y + size / 2)
                + " sound=" + (SoundButton.X + size / 2) + "," + (SoundButton.Y + size / 2));
        }
        return NetworkButton.X;
    }
    private int _loggedX = int.MinValue, _loggedY;

    // ---------- What to show.
    private static uint Uid => CredentialContext.Current.Uid;
    private static (string Headline, string Detail, bool Online) NetworkWords()
    {
        var adapter = Cosmos.Kernel.System.Network.NetworkManager.Primary;
        if (!adapter.IsValid) { return ("No network adapter", "Zenith supports Intel E1000E wired adapters", false); }
        if (NetworkService.Disconnected) { return ("Disconnected", "Wired · until you connect again", false); }
        if (NetworkService.Configuration.Mode == "off") { return ("Network turned off", "Wired · off in the connection settings", false); }
        if (!adapter.LinkUp) { return ("The cable is out", "Wired · no link", false); }
        string address = adapter.IPConfig?.Address.ToString() ?? "0.0.0.0";
        if (NetworkService.Online && address != "0.0.0.0")
        { return ("Connected", "Wired · " + address + (NetworkService.Configuration.Mode == "static" ? " (manual)" : ""), true); }
        return (NetworkService.Busy ? "Connecting..." : "Not connected", "Wired · " + (NetworkService.Configuration.Mode == "dhcp" ? "no answer from DHCP" : "the manual settings did not connect"), false);
    }
    private static string Rate(uint bytesPerSecond)
    {
        ulong b = (ulong)bytesPerSecond * 8;
        if (b < 1000) { return b + " b/s"; }
        string[] units = { " kb/s", " Mb/s", " Gb/s" }; int u = 0; ulong scale = 1000;
        while (u < 2 && b >= scale * 1000) { scale *= 1000; u++; }
        ulong whole = b / scale, tenth = b % scale * 10 / scale;
        return whole + (whole < 100 ? "." + tenth : "") + units[u];
    }

    /// <summary>What the two buttons show (online, the state words, the output, volume and mute): the bar redraws when
    /// it changes, whoever changed it (Beacon, Meridian, the wheel, a cable).</summary>
    public long StateKey()
    {
        var (headline, _, online) = NetworkWords();
        var (output, _) = AudioService.Overview(Uid);
        var hash = new HashCode();
        hash.Add(online); hash.Add(headline); hash.Add(output.Available); hash.Add(output.Muted); hash.Add(output.Volume); hash.Add(_busy);
        return hash.ToHashCode();
    }

    // ---------- Buttons on the bar.
    public void DrawButtons(Surface surface, int mouseX, int mouseY)
    {
        var (_, _, online) = NetworkWords();
        Button(surface, NetworkButton, "network", online ? Theme.TextPrimary : Theme.TextMuted, Open == Panel.Network, mouseX, mouseY);
        if (!online) { surface.FillRoundRect(new Rect(NetworkButton.Right - 11, NetworkButton.Y + 7, 6, 6), 3, Theme.Danger); }
        var (output, _) = AudioService.Overview(Uid);
        bool silent = !output.Available || output.Muted || output.Volume == 0;
        Button(surface, SoundButton, "volume-2", output.Available ? Theme.TextPrimary : Theme.TextMuted, Open == Panel.Sound, mouseX, mouseY);
        if (silent) { Strike(surface, SoundButton.X + (SoundButton.W - 16) / 2, SoundButton.Y + (SoundButton.H - 16) / 2, output.Available ? Theme.Danger : Theme.TextMuted); }
    }
    /// <summary>A diagonal stroke over a 16-pixel icon: muted or no device (zenith-icons has no volume-x at its file limit).</summary>
    private static void Strike(Surface surface, int x, int y, uint color)
    {
        for (int i = 0; i < 16; i++) { surface.FillRect(new Rect(x + i, y + i, 2, 2), color); }
    }
    private static void Button(Surface surface, Rect b, string icon, uint color, bool open, int mouseX, int mouseY)
    {
        if (open) { surface.FillRoundRect(b, Theme.ControlRadius, Theme.AccentSoft); }
        else if (b.Contains(mouseX, mouseY)) { surface.FillRoundRect(b, Theme.ControlRadius, Theme.Hover); }
        if (!Icons.Draw(surface, icon, b.X + (b.W - 16) / 2, b.Y + (b.H - 16) / 2, 16, color))
        { surface.DrawTextCentered(icon.StartsWith("volume") ? "V" : "N", Fonts.SemiBold, Theme.TextTitle, color, b, true); }
    }

    // ---------- The panels.
    public void DrawPanel(Surface surface, Rect bar, int mouseX, int mouseY)
    {
        _actions.Clear(); _slider = new Rect(0, 0, 0, 0);
        if (Open == Panel.None) { return; }
        int height = Open == Panel.Network ? NetworkHeight() : SoundHeight();
        Rect anchor = Open == Panel.Network ? NetworkButton : SoundButton;
        int x = Math.Max(bar.X, Math.Min(anchor.Right - PanelWidth, bar.Right - PanelWidth));
        _panel = new Rect(x, bar.Y - height - Theme.TaskbarMargin + Zenith.Gui.Motion.OverlayMotion.Offset(Zenith.Gui.Motion.OverlayMotion.Centers), PanelWidth, height);
        surface.DrawShadow(_panel, Theme.WindowRadius, 18, 100, 6);
        surface.FillRoundRect(_panel, Theme.WindowRadius, Theme.Menu);
        surface.StrokeRoundRect(_panel, Theme.WindowRadius, Theme.PanelBorder);
        if (Open == Panel.Network) { DrawNetwork(surface, mouseX, mouseY); } else { DrawSound(surface, mouseX, mouseY); }
    }
    private void DrawNetwork(Surface surface, int mouseX, int mouseY)
    {
        int x = _panel.X + Padding, y = _panel.Y + Padding, w = _panel.W - 2 * Padding;
        var (headline, detail, online) = NetworkWords();
        surface.FillRoundRect(new Rect(x, y + 5, 10, 10), 5, online ? Theme.Accent : Theme.Danger);
        surface.DrawText(headline, Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, x + 18, y);
        surface.DrawText(detail, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x + 18, y + 22);
        y += 50;
        // The last minute of traffic: down above the line, up below it.
        var (rateIn, rateOut) = NetworkCenter.Rates;
        surface.DrawText("Down " + Rate(rateIn) + "   Up " + Rate(rateOut), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, y);
        y += 20;
        Rect chart = new(x, y, w, 64); surface.FillRoundRect(chart, 6, Theme.Hover);
        byte[] h = NetworkCenter.History.Encode(0);
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
        uint peak = 12500; for (int i = 0; i < n; i++) { peak = Math.Max(peak, Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16 + i * 8)), BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20 + i * 8)))); }
        int bar = Math.Max(1, (chart.W - 8) / 60), mid = chart.Y + chart.H / 2;
        for (int i = 0; i < n; i++)
        {
            uint down = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16 + i * 8)), up = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(20 + i * 8));
            int bx = chart.Right - 4 - (n - i) * bar, dh = (int)((ulong)down * (ulong)(chart.H / 2 - 4) / peak), uh = (int)((ulong)up * (ulong)(chart.H / 2 - 4) / peak);
            if (dh > 0) { surface.FillRect(new Rect(bx, mid - dh, Math.Max(1, bar - 1), dh), Theme.Accent); }
            if (uh > 0) { surface.FillRect(new Rect(bx, mid + 1, Math.Max(1, bar - 1), uh), Theme.TextSecondary); }
        }
        surface.FillRect(new Rect(chart.X + 4, mid, chart.W - 8, 1), Theme.PanelBorder);
        y += chart.H + 12;
        bool connected = online || NetworkService.Busy;
        Action(surface, new Rect(x, y, (w - 8) / 2, 32), connected ? "Disconnect" : "Connect", (int)Act.Toggle, mouseX, mouseY, !_busy);
        Action(surface, new Rect(x + (w - 8) / 2 + 8, y, (w - 8) / 2, 32), "Diagnostics", (int)Act.Diagnostics, mouseX, mouseY, true);
        y += 40;
        Action(surface, new Rect(x, y, (w - 8) / 2, 32), "Open Beacon", (int)Act.Beacon, mouseX, mouseY, true);
        Action(surface, new Rect(x + (w - 8) / 2 + 8, y, (w - 8) / 2, 32), "Network settings", (int)Act.NetworkSettings, mouseX, mouseY, true);
        y += 40;
        string note = NetworkNote;
        if (note.Length > 0) { surface.DrawText(Fit(note, w), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, y); }
    }
    private string NetworkNote => _note.Length > 0 ? _note : CredentialContext.Current.IsRoot ? "" : "Connecting and disconnecting ask for an administrator in Beacon.";
    // Rows: headline 50, rates 20, chart 64 + 12, two button rows 40 each, the note 22 when there is one, padding.
    private int NetworkHeight() => Padding + 50 + 20 + 64 + 12 + 40 + 40 + (NetworkNote.Length > 0 ? 22 : 0) + Padding - 8;
    private int SoundHeight()
    {
        var (_, streams) = AudioService.Overview(Uid);
        return 196 + Math.Min(4, streams.Count) * 26 + (streams.Count == 0 ? 0 : 22) + (_note.Length > 0 ? 22 : 0);
    }
    private void DrawSound(Surface surface, int mouseX, int mouseY)
    {
        int x = _panel.X + Padding, y = _panel.Y + Padding, w = _panel.W - 2 * Padding;
        var (output, streams) = AudioService.Overview(Uid);
        surface.DrawText("Sound", Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, x, y);
        surface.DrawText(Fit(output.Name, w), Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, y + 22);
        y += 52;
        bool usable = output.Available && output.Id != 0;
        Action(surface, new Rect(x, y, 36, 32), "", (int)Act.Mute, mouseX, mouseY, usable);
        Icons.Draw(surface, "volume-2", x + 10, y + 8, 16, usable ? Theme.TextPrimary : Theme.TextMuted);
        if (output.Muted || output.Volume == 0 || !usable) { Strike(surface, x + 10, y + 8, usable ? Theme.Danger : Theme.TextMuted); }
        _slider = new Rect(x + 46, y + 12, w - 46 - 48, 8);
        surface.FillRoundRect(_slider, 4, Theme.Hover);
        int filled = (int)((long)_slider.W * output.Volume / 100);
        if (filled > 0) { surface.FillRoundRect(new Rect(_slider.X, _slider.Y, filled, _slider.H), 4, output.Muted ? Theme.TextMuted : Theme.Accent); }
        surface.FillRoundRect(new Rect(_slider.X + filled - 7, _slider.Y - 4, 14, 16), 7, usable ? Theme.TextPrimary : Theme.TextMuted);
        surface.DrawText(output.Muted ? "Muted" : output.Volume + "%", Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, _slider.Right + 10, y + 8);
        y += 44;
        Action(surface, new Rect(x, y, (w - 8) / 2, 30), "- 5%", (int)Act.Down, mouseX, mouseY, usable && output.Volume > 0);
        Action(surface, new Rect(x + (w - 8) / 2 + 8, y, (w - 8) / 2, 30), "+ 5%", (int)Act.Up, mouseX, mouseY, usable && output.Volume < 100);
        y += 40;
        if (streams.Count > 0)
        {
            surface.DrawText("Playing", Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, x, y); y += 22;
            for (int i = 0; i < Math.Min(4, streams.Count); i++)
            {
                var s = streams[i];
                surface.DrawText(Fit(s.Name.Length > 0 ? s.Name : "Program", w - 90), Fonts.Regular, Theme.TextSmall, s.Muted ? Theme.TextMuted : Theme.TextPrimary, x, y);
                Rect meter = new(x + w - 80, y + 5, 80, 6); surface.FillRoundRect(meter, 3, Theme.Hover);
                int level = Math.Clamp(s.Peak * 80 / 32768, 0, 80); if (level > 0) { surface.FillRoundRect(new Rect(meter.X, meter.Y, level, 6), 3, Theme.Accent); }
                y += 26;
            }
        }
        Action(surface, new Rect(x, _panel.Bottom - Padding - 32, w, 32), "Sound settings", (int)Act.SoundSettings, mouseX, mouseY, true);
        // The note has its own row above the settings button (the panel grew by it), never over the volume buttons.
        if (_note.Length > 0) { surface.DrawText(Fit(_note, w), Fonts.Regular, Theme.TextSmall, Theme.Danger, x, _panel.Bottom - Padding - 32 - 22); }
    }
    private void Action(Surface surface, Rect r, string label, int action, int mouseX, int mouseY, bool enabled)
    {
        surface.FillRoundRect(r, Theme.ControlRadius, r.Contains(mouseX, mouseY) && enabled ? Theme.Pressed : Theme.Hover);
        if (label.Length > 0) { surface.DrawTextCentered(label, Fonts.Regular, Theme.TextTitle, enabled ? Theme.TextPrimary : Theme.TextMuted, r, true); }
        if (enabled) { _actions.Add((r, action)); }
    }
    private static string Fit(string text, int width)
    {
        if (Fonts.Regular.MeasureString(text, Theme.TextSmall) <= width) { return text; }
        for (int n = text.Length - 1; n > 0; n--) { string cut = text.Substring(0, n).TrimEnd() + "..."; if (Fonts.Regular.MeasureString(cut, Theme.TextSmall) <= width) { return cut; } }
        return "";
    }

    // ---------- Input.
    public bool PanelContains(int x, int y) => Open != Panel.None && _panel.Contains(x, y);
    /// <summary>A click: on a button toggles its panel; inside a panel runs its action. True when it was ours.</summary>
    public bool HandleClick(int x, int y)
    {
        if (NetworkButton.Contains(x, y)) { Toggle(Panel.Network); return true; }
        if (SoundButton.Contains(x, y)) { Toggle(Panel.Sound); return true; }
        if (Open == Panel.None) { return false; }
        if (!_panel.Contains(x, y)) { Open = Panel.None; _note = ""; return false; }
        if (_slider.W > 0 && new Rect(_slider.X - 8, _slider.Y - 10, _slider.W + 16, _slider.H + 20).Contains(x, y))
        { Sliding = true; Slide(x); return true; }
        foreach (var (rect, action) in _actions) { if (rect.Contains(x, y)) { Run((Act)action); break; } }
        return true;
    }
    /// <summary>The volume knob follows the pointer while the button is held.</summary>
    public bool Sliding { get; private set; }
    public void Slide(int x)
    {
        if (_slider.W <= 0) { Sliding = false; return; }
        int value = (int)Math.Round(Math.Clamp(x - _slider.X, 0, _slider.W) * 100.0 / _slider.W);
        var (output, _) = AudioService.Overview(Uid);
        if (value != output.Volume) { Volume(value); }
    }
    public void StopSliding() { Sliding = false; }
    private void Toggle(Panel panel)
    {
        Open = Open == panel ? Panel.None : panel; _note = "";
        if (Open != Panel.None) { Zenith.Gui.Motion.OverlayMotion.Start(Zenith.Gui.Motion.OverlayMotion.Centers); }
        Zenith.Core.Log.Write("taskbar", (panel == Panel.Network ? "network" : "sound") + " center " + (Open == panel ? "opened" : "closed"));
    }
    /// <summary>The wheel over the sound button (or the open sound panel) changes the volume in 5% steps.</summary>
    public bool Scroll(int x, int y, int delta)
    {
        if (!(SoundButton.Contains(x, y) || (Open == Panel.Sound && _panel.Contains(x, y))) || delta == 0) { return false; }
        var (output, _) = AudioService.Overview(Uid);
        // Positive is the wheel turned down (toward you): quieter.
        Volume(output.Volume + (delta > 0 ? -5 : 5)); return true;
    }
    private void Volume(int value)
    {
        long r = AudioService.SetOutput(Uid, Math.Clamp(value, 0, 100), null);
        _note = r == 0 ? "" : r == -13 ? "Sound belongs to the signed-in session." : "The volume could not be changed.";
        if (r == 0) { Zenith.Core.Log.Write("taskbar", "volume " + Math.Clamp(value, 0, 100)); Zenith.Core.Audio.SystemSounds.Play("volume"); }
    }
    private void Run(Act act)
    {
        _note = "";
        switch (act)
        {
            case Act.Beacon: Open = Panel.None; _openApp("Beacon", null); break;
            case Act.Diagnostics: Open = Panel.None; _openApp("Beacon", new[] { "diagnostics" }); break;
            case Act.NetworkSettings: Open = Panel.None; _openApp("Meridian", new[] { "--page", "network.connections" }); break;
            case Act.SoundSettings: Open = Panel.None; _openApp("Meridian", new[] { "--page", "audio.playback" }); break;
            case Act.Mute:
            {
                var (output, _) = AudioService.Overview(Uid);
                long r = AudioService.SetOutput(Uid, output.Volume, !output.Muted);
                if (r != 0) { _note = "Sound belongs to the signed-in session."; }
                else { Zenith.Core.Log.Write("taskbar", output.Muted ? "sound unmuted" : "sound muted"); }
                break;
            }
            case Act.Down: case Act.Up: { var (output, _) = AudioService.Overview(Uid); Volume(output.Volume + (act == Act.Up ? 5 : -5)); break; }
            case Act.Toggle:
                if (!CredentialContext.Current.IsRoot) { Open = Panel.None; _openApp("Beacon", null); break; }
                // DHCP can take seconds: the desktop keeps drawing while a worker connects or disconnects.
                bool disconnect = NetworkWords().Online || NetworkService.Busy;
                _busy = true;
                new Thread(() =>
                {
                    using var root = CredentialContext.Enter(Credentials.Root);
                    try
                    {
                        if (disconnect) { NetworkService.Disconnect(); } else { NetworkService.Apply(() => { }); }
                        Zenith.Core.Log.Write("taskbar", disconnect ? "network disconnected" : "network connected");
                    }
                    catch (Exception e) { _note = e.Message.Length > 80 ? e.Message[..80] : e.Message; }
                    _busy = false;
                }).Start();
                break;
        }
    }
    /// <summary>Hover key part for the desktop's redraw decisions.</summary>
    public int HoverKey(int x, int y)
    {
        if (Sliding) { return 800 + AudioService.Overview(Uid).Output.Volume; }
        if (NetworkButton.Contains(x, y)) { return 700; }
        if (SoundButton.Contains(x, y)) { return 701; }
        if (Open != Panel.None && _panel.Contains(x, y)) { for (int i = 0; i < _actions.Count; i++) { if (_actions[i].Rect.Contains(x, y)) { return 710 + i; } } return 709; }
        return 0;
    }
}
