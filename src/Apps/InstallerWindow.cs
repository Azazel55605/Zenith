using System;
using System.Collections.Generic;
using System.Threading;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Storage;
using Zenith.Core;
using Zenith.Core.Configuration;
using Zenith.Core.Input;
using Zenith.Core.Packages;
using Zenith.Core.Time;
using Zenith.Core.Security;
using Zenith.Core.Storage;
using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

/// <summary>Small guided installer. The worker never touches graphics or logs secrets.</summary>
internal sealed class InstallerWindow : Window
{
    private enum Step { Region, Disk, Accounts, Confirm, Installing, Done, Unavailable, Error }
    private readonly object _gate = new();
    private readonly List<IBlockDevice> _disks;
    private readonly string[] _values = { "zenith", "user", "", "", "", "" };
    private static readonly string[] Labels = { "Hostname", "User name", "User password", "Repeat user password", "Root password", "Repeat root password" };
    private const int SidebarWidth = 168, BackX = SidebarWidth + 44, NextInset = 162;
    private static readonly string[] StepNames = { "Region", "Disk", "Accounts", "Review", "Install", "Finish" };
    private const int RegionRows = 7, ZoneRows = 7, RegionRowHeight = 32;
    private readonly string[] _layouts = KeyboardLayoutCatalog.Names;
    private int _layout, _zone, _zoneTop, _regionFocus;
    private string[] _apps = Array.Empty<string>();
    private string _appsLine = "", _trustLine = "", _networkLine = "";
    private int _networkCheck;
    private Step _step;
    private int _selected, _field;
    private string _message = "";
    private bool _dirty = true;
    private bool _erase;
    public InstallerWindow() : base("Sunrise - Install Zenith", 780, 530)
    {
        _disks = Installer.Candidates();
        Log.Write("installer", "Sunrise ready");
        var settings = SystemConfiguration.Settings.Current;
        _layout = Math.Max(0, Array.IndexOf(_layouts, settings.Keyboard));
        for (int i = 0; i < TimeZones.All.Count; i++) { if (TimeZones.All[i].Name == settings.TimeZone) { _zone = i; } }
        _zoneTop = Math.Max(0, Math.Min(_zone - ZoneRows / 2, TimeZones.All.Count - ZoneRows));
        if (SystemMounts.Mode != BootMode.Live)
        { _step = Step.Unavailable; _message = "Zenith is running from an installed root. To install onto another disk, boot the ISO live rescue session as root."; }
        else if (!Identity.IsRoot)
        { _step = Step.Unavailable; _message = "Start Sunrise as root from the ISO live rescue session."; }
    }
    public override bool CanResize => false;
    public override bool AllowMaterials { get { lock (_gate) { return _step != Step.Accounts; } } }
    public override bool CanClose { get { lock (_gate) { return _step != Step.Installing; } } }
    public override void Close() { if (CanClose) { ClearSecrets(); base.Close(); } }
    public override void EndSession() { if (CanClose) { ClearSecrets(); Close(); } }
    public override bool Update() { lock (_gate) { bool dirty = _dirty; _dirty = false; return dirty; } }
    private void ClearSecrets() { for (int i = 2; i < _values.Length; i++) { _values[i] = ""; } }
    private void SetStatus(string message) { lock (_gate) { _message = message; _dirty = true; } }
    public override bool AllowBackgroundMaterials => true;

    public override void DrawContent(Surface surface, Rect content)
    {
        lock (_gate)
        {
            Rect detail = PaintWorkspace(surface, content, SidebarWidth);
            DrawSteps(surface, content);
            int x = detail.X + 20, y = content.Y + AppStyle.Inset, width = detail.W - 40;
            Text(surface, "Install Zenith", x, y, Theme.TextHeading);
            Text(surface, "Test in a VM first. Keep the live ISO for recovery.", x, y + 34, 12, Theme.TextSecondary);
            y = content.Y + 86;
            switch (_step)
            {
                case Step.Region:
                    DrawRegion(surface, x, y, width);
                    break;
                case Step.Disk:
                    Text(surface, "Choose a disk. All existing data on it will be erased.", x, y, 14);
                    for (int i = 0; i < 6 && _selected / 6 * 6 + i < _disks.Count; i++)
                    {
                        int index = _selected / 6 * 6 + i; var d = _disks[index]; var box = new Rect(x, y + 32 + i * 42, width, 36);
                        AppStyle.NavigationRow(surface, box, d.Name + "  " + (d.BlockCount * d.BlockSize / (1024 * 1024)) + " MiB  |  " + StorageManager.GetPartitions(d).Count + " existing partitions", index == _selected);
                    }
                    if (_disks.Count == 0) { Text(surface, "No eligible disk (minimum 256 MiB).", x, y + 40, 14); }
                    break;
                case Step.Accounts:
                    for (int i = 0; i < _values.Length; i++)
                    {
                        Text(surface, Labels[i], x, y + i * 42 + 9, 13);
                        var box = new Rect(x + 190, y + i * 42, width - 190, AppStyle.ControlHeight);
                        string value = i < 2 ? _values[i] : new string('*', _values[i].Length);
                        value += i == _field ? "|" : "";
                        while (value.Length > 0 && Fonts.Regular.MeasureString(value, Theme.TextBody) > box.W - 24) { value = value[1..]; }
                        AppStyle.Field(surface, box, value, i == _field);
                    }
                    break;
                case Step.Confirm:
                    Text(surface, "Erase " + _disks[_selected].Name + " and install Zenith?", x, y, 18);
                    Text(surface, "128 MiB EFI partition + ext2 root (rest of the disk, up to 2 TiB)", x, y + 34, 13, Theme.TextSecondary);
                    Text(surface, "Hostname: " + _values[0] + "   User: " + _values[1], x, y + 62, 14);
                    Text(surface, "Keyboard: " + _layouts[_layout] + "   Time zone: " + TimeZones.All[_zone].Name, x, y + 88, 14);
                    Text(surface, _appsLine, x, y + 114, 14);
                    Text(surface, _trustLine, x, y + 140, 14);
                    Text(surface, "Network: " + _networkLine, x, y + 166, 14, Volatile.Read(ref _networkCheck) == 3 ? Theme.Danger : null);
                    Text(surface, "This cannot be cancelled once disk writing starts.", x, y + 196, 13, Theme.TextSecondary);
                    AppStyle.Checkbox(surface, new Rect(x, y + 222, width, 36), "I understand that this disk will be erased (Space)", _erase, true);
                    break;
                case Step.Installing:
                    Text(surface, "Installing... keep this VM running.", x, y, 18);
                    break;
                case Step.Done:
                    Text(surface, "Installation complete", x, y, 18);
                    Text(surface, "Power off, remove the ISO and boot the installed disk.", x, y + 40, 14);
                    Text(surface, "Log in with the account and password you selected.", x, y + 72, 14);
                    break;
                case Step.Unavailable:
                    Text(surface, "Installer requires a live session", x, y, 18);
                    Text(surface, "No installation was attempted in this window.", x, y + 38, 14);
                    Text(surface, "Use boot status in Terminal to inspect the current boot.", x, y + 70, 14);
                    break;
                case Step.Error:
                    Text(surface, "Installation did not complete", x, y, 18);
                    Text(surface, "Inspect the error. A failed write may require reinstalling.", x, y + 38, 14);
                    break;
            }
            int messageY = content.Y + 365;
            foreach (string line in Fonts.Wrap(_message, Fonts.Regular, 13, width))
            { Text(surface, line, x, messageY, 13, _step == Step.Error ? Theme.Danger : Theme.TextSecondary); messageY += 16; }
            if (_step is Step.Region or Step.Disk or Step.Accounts or Step.Confirm)
            {
                Button(surface, content, BackX, _step == Step.Region ? "Cancel" : "Back");
                Button(surface, content, content.W - NextInset, _step == Step.Confirm ? "Erase and install" : "Next", true, _step != Step.Confirm || _erase, _step == Step.Confirm);
            }
            else if (_step is Step.Done or Step.Unavailable or Step.Error) { Button(surface, content, BackX, "Close"); }
        }
    }
    private void DrawRegion(Surface surface, int x, int y, int width)
    {
        int column = (width - 20) / 2, right = x + column + 20;
        Text(surface, "Keyboard layout", x, y, 14, _regionFocus == 0 ? Theme.TextPrimary : Theme.TextSecondary);
        Text(surface, "Time zone", right, y, 14, _regionFocus == 1 ? Theme.TextPrimary : Theme.TextSecondary);
        for (int i = 0; i < RegionRows && i < _layouts.Length; i++)
        {
            string label = _layouts[i];
            foreach (var (name, description) in KeyboardLayouts.All) { if (name == _layouts[i]) { label = description; } }
            AppStyle.NavigationRow(surface, new Rect(x, y + 26 + i * RegionRowHeight, column, RegionRowHeight - 4), label, i == _layout);
        }
        var zones = TimeZones.All;
        for (int i = 0; i < ZoneRows && _zoneTop + i < zones.Count; i++)
        {
            int index = _zoneTop + i;
            AppStyle.NavigationRow(surface, new Rect(right, y + 26 + i * RegionRowHeight, column, RegionRowHeight - 4), zones[index].Name, index == _zone);
        }
        Text(surface, "Applied now, so the passwords you type next use this layout. Tab switches lists.", x, y + 26 + RegionRows * RegionRowHeight + 6, 12, Theme.TextSecondary);
    }
    private void SelectLayout(int index)
    {
        index = Math.Clamp(index, 0, _layouts.Length - 1);
        if (index == _layout) { return; }
        SystemConfiguration.Settings.SetKeyboard(_layouts[index]); _layout = index;
        Log.Write("installer", "keyboard " + _layouts[index]);
    }
    private void SelectZone(int index)
    {
        index = Math.Clamp(index, 0, TimeZones.All.Count - 1);
        if (index == _zone) { return; }
        SystemConfiguration.Settings.SetTimeZone(TimeZones.All[index].Name); _zone = index;
        if (_zone < _zoneTop) { _zoneTop = _zone; }
        else if (_zone >= _zoneTop + ZoneRows) { _zoneTop = _zone - ZoneRows + 1; }
    }
    /// <summary>What the review shows about the seed, and the network check in the background.</summary>
    private void PrepareReview()
    {
        var seed = NativeSeed.Bundle;
        try
        {
            _apps = NativeSeed.Check().ToArray();
            _appsLine = _apps.Length == 0 ? "Apps: none on this ISO (base system; add apps later with pkg)" : "Apps: " + _apps.Length + " packages, including Meridian, Polaris and Equinox";
        }
        catch (Exception e) { _apps = Array.Empty<string>(); _appsLine = "Apps: the ISO's packages cannot be installed: " + e.Message; }
        var key = seed?.Single(SeedKind.PackageKey); var ca = seed?.Single(SeedKind.TlsCa);
        _trustLine = seed is null ? "Updates: not configured on this ISO (pkg trust, tls trust, pkg mirror)"
            : "Updates: " + (seed.Mirror ?? "no mirror") + (key is null ? "" : ", key " + key.Sha256[..12]) + (ca is null ? "" : ", CA " + ca.Sha256[..12]);
        if (seed?.Mirror is null) { _networkLine = "skipped (no mirror)"; Volatile.Write(ref _networkCheck, 0); return; }
        if (Volatile.Read(ref _networkCheck) == 1) { return; }   // a check is still running
        Volatile.Write(ref _networkCheck, 1); _networkLine = "checking the mirror...";
        var identity = Identity;
        new Thread(() =>
        {
            using var caller = CredentialContext.Enter(identity);
            string line; int state;
            try { line = NativeSeed.CheckMirror(() => { }); state = 2; }
            catch (Exception e) { line = "mirror not reachable: " + e.Message + " (installing works offline)"; state = 3; }
            Log.Write("installer", "network check: " + line);
            lock (_gate) { _networkLine = line; Volatile.Write(ref _networkCheck, state); _dirty = true; }
        }).Start();
    }
    private void DrawSteps(Surface surface, Rect content)
    {
        surface.DrawText("Sunrise", Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, content.X + 24, content.Y + 24);
        int current = _step switch { Step.Region => 0, Step.Disk => 1, Step.Accounts => 2, Step.Confirm => 3, Step.Installing => 4, _ => 5 };
        for (int i = 0; i < StepNames.Length; i++)
        {
            var row = new Rect(content.X + 12, content.Y + 56 + i * 40, SidebarWidth, 36);
            AppStyle.NavigationRow(surface, row, StepNames[i], i == current);
        }
    }
    private static void Text(Surface s, string text, int x, int y, int size, uint? color = null)
        => s.DrawText(text, Fonts.Regular, size, color ?? Theme.TextPrimary, x, y);
    private static void Button(Surface s, Rect c, int x, string label, bool primary = false, bool enabled = true, bool danger = false)
    {
        var b = new Rect(c.X + x, c.Bottom - 60, 130, AppStyle.ControlHeight);
        AppStyle.Button(s, b, label, primary && !danger, enabled, danger);
    }
    public override void OnMouseDown(int x, int y)
    {
        lock (_gate)
        {
            if (_step == Step.Installing) { return; }
            if (y >= Content.H - 60 && y < Content.H - 24)
            {
                if (x >= BackX && x < BackX + 130) { Back(); }
                else if (x >= Content.W - NextInset && x < Content.W - NextInset + 130) { Next(); }
            }
            else if (x < SidebarWidth + 24) { return; }
            else if (_step == Step.Region && y >= 112 && y < 112 + RegionRows * RegionRowHeight)
            {
                int detailX = SidebarWidth + 20, column = (Content.W - SidebarWidth - 40 - 20) / 2, row = (y - 112) / RegionRowHeight;
                if (x < detailX + column) { _regionFocus = 0; SelectLayout(row); }
                else { _regionFocus = 1; SelectZone(_zoneTop + row); }
            }
            else if (_step == Step.Disk && y >= 118)
            { _selected = Math.Clamp(_selected / 6 * 6 + (y - 118) / 42, 0, Math.Max(0, _disks.Count - 1)); }
            else if (_step == Step.Accounts && y >= 86 && y < 338) { _field = (y - 86) / 42; }
            else if (_step == Step.Confirm && y >= 308 && y < 344) { _erase = !_erase; }
            _dirty = true;
        }
    }
    public override void OnKey(KeyEvent key)
    {
        lock (_gate)
        {
            if (_step == Step.Installing) { return; }
            if (key.Key == ConsoleKeyEx.Escape) { Back(); }
            else if (_step == Step.Accounts)
            {
                if (key.Key == ConsoleKeyEx.Tab) { _field = (_field + ((key.Modifiers & ConsoleModifiers.Shift) != 0 ? 5 : 1)) % 6; }
                else if (key.Key == ConsoleKeyEx.Enter) { if (_field == 5) { Next(); } else { _field++; } }
                else if (key.Key == ConsoleKeyEx.Backspace && _values[_field].Length > 0) { _values[_field] = _values[_field][..^1]; }
                else if (key.KeyChar >= ' ' && key.KeyChar <= '~' && _values[_field].Length < 128) { _values[_field] += key.KeyChar; }
            }
            else if (_step == Step.Confirm && key.KeyChar == ' ') { _erase = !_erase; }
            else if (_step == Step.Region && key.Key == ConsoleKeyEx.Tab) { _regionFocus ^= 1; }
            else if (_step == Step.Region && key.Key is ConsoleKeyEx.DownArrow or ConsoleKeyEx.UpArrow)
            {
                int delta = key.Key == ConsoleKeyEx.DownArrow ? 1 : -1;
                if (_regionFocus == 0) { SelectLayout(_layout + delta); } else { SelectZone(_zone + delta); }
            }
            else if (_step == Step.Disk && key.Key == ConsoleKeyEx.DownArrow) { _selected = Math.Min(_selected + 1, _disks.Count - 1); }
            else if (_step == Step.Disk && key.Key == ConsoleKeyEx.UpArrow) { _selected = Math.Max(0, _selected - 1); }
            else if (key.Key == ConsoleKeyEx.Enter) { Next(); }
            _dirty = true;
        }
    }
    private void Back()
    {
        _message = ""; _erase = false;
        if (_step == Step.Confirm) { _step = Step.Accounts; }
        else if (_step == Step.Accounts) { _step = Step.Disk; }
        else if (_step == Step.Disk) { _step = Step.Region; }
        else { ClearSecrets(); Close(); }
    }
    private void Next()
    {
        try
        {
            if (_step == Step.Region) { _step = Step.Disk; Log.Write("installer", "region keyboard=" + _layouts[_layout] + " timezone=" + TimeZones.All[_zone].Name); }
            else if (_step == Step.Disk) { if (_disks.Count == 0) { return; } _step = Step.Accounts; Log.Write("installer", "account setup"); }
            else if (_step == Step.Accounts)
            {
                InstallationProfile.ValidateNames(_values[0], _values[1]);
                if (_values[2].Length == 0 || _values[4].Length == 0 || _values[2] != _values[3] || _values[4] != _values[5])
                { throw new ArgumentException("Enter both passwords and matching confirmations."); }
                _step = Step.Confirm; _erase = false; PrepareReview(); Log.Write("installer", "review disk " + _disks[_selected].Name);
            }
            else if (_step == Step.Confirm)
            {
                if (!_erase) { throw new ArgumentException("Confirm disk erasure first."); }
                string[] values = (string[])_values.Clone(); ClearSecrets();
                var disk = _disks[_selected]; _step = Step.Installing; _message = "Preparing account credentials";
                new Thread(() =>
                {
                    using var caller = CredentialContext.Enter(Identity);
                    try
                    {
                        Installer.InstallConfigured(disk, SetStatus, () =>
                        {
                            var profile = InstallationProfile.Create(values[0], values[1], values[4], values[5], values[2], values[3], HardwareEntropy.Source);
                            Array.Clear(values, 2, 4);
                            return profile;
                        });
                        lock (_gate) { _step = Step.Done; _message = "After boot, test a signed kernel update before using this system."; _dirty = true; }
                        Log.Write("installer", "guided installation complete");
                    }
                    catch (Exception e)
                    {
                        Array.Clear(values, 2, 4);
                        lock (_gate) { _step = Step.Error; _message = e.Message; _dirty = true; }
                        Log.Write("installer", "guided installation failed: " + e.Message);
                    }
                }).Start();
            }
            _message = _step == Step.Installing ? _message : "";
        }
        catch (Exception e) { if (_step == Step.Installing) { _step = Step.Error; ClearSecrets(); } _message = e.Message; Log.Write("installer", "validation: " + e.Message); }
    }
}
