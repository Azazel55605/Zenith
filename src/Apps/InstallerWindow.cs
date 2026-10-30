using System;
using System.Collections.Generic;
using System.Threading;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Storage;
using Zenith.Core;
using Zenith.Core.Security;
using Zenith.Core.Storage;
using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

/// <summary>Small guided installer. The worker never touches graphics or logs secrets.</summary>
internal sealed class InstallerWindow : Window
{
    private enum Step { Disk, Accounts, Confirm, Installing, Done, Unavailable, Error }
    private readonly object _gate = new();
    private readonly List<IBlockDevice> _disks;
    private readonly string[] _values = { "zenith", "user", "", "", "", "" };
    private static readonly string[] Labels = { "Hostname", "User name", "User password", "Repeat user password", "Root password", "Repeat root password" };
    private const int SidebarWidth = 168, BackX = SidebarWidth + 44, NextInset = 162;
    private static readonly string[] StepNames = { "Disk", "Accounts", "Review", "Install", "Finish" };
    private Step _step;
    private int _selected, _field;
    private string _message = "";
    private bool _dirty = true;
    private bool _erase;
    public InstallerWindow() : base("Sunrise - Install Zenith", 780, 530)
    {
        _disks = Installer.Candidates();
        Log.Write("installer", "Sunrise ready");
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
                    Text(surface, "128 MiB EFI partition + ext2 root (up to 512 MiB)", x, y + 38, 14);
                    Text(surface, "Hostname: " + _values[0] + "   User: " + _values[1], x, y + 70, 14);
                    Text(surface, "Your live session remains separate from the target.", x, y + 102, 14);
                    Text(surface, "This cannot be cancelled once disk writing starts.", x, y + 134, 14);
                    Text(surface, (_erase ? "[x]" : "[ ]") + " I understand that this disk will be erased (Space)", x, y + 182, 14);
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
            if (_step is Step.Disk or Step.Accounts or Step.Confirm)
            {
                Button(surface, content, BackX, _step == Step.Disk ? "Cancel" : "Back");
                Button(surface, content, content.W - NextInset, _step == Step.Confirm ? "Erase and install" : "Next", true, _step != Step.Confirm || _erase, _step == Step.Confirm);
            }
            else if (_step is Step.Done or Step.Unavailable or Step.Error) { Button(surface, content, BackX, "Close"); }
        }
    }
    private void DrawSteps(Surface surface, Rect content)
    {
        surface.DrawText("Sunrise", Fonts.SemiBold, Theme.TextBody, Theme.TextPrimary, content.X + 24, content.Y + 24);
        int current = _step switch { Step.Disk => 0, Step.Accounts => 1, Step.Confirm => 2, Step.Installing => 3, _ => 4 };
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
            else if (_step == Step.Disk && y >= 118)
            { _selected = Math.Clamp(_selected / 6 * 6 + (y - 118) / 42, 0, Math.Max(0, _disks.Count - 1)); }
            else if (_step == Step.Accounts && y >= 86 && y < 338) { _field = (y - 86) / 42; }
            else if (_step == Step.Confirm && y >= 256 && y < 294) { _erase = !_erase; }
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
        else { ClearSecrets(); Close(); }
    }
    private void Next()
    {
        try
        {
            if (_step == Step.Disk) { if (_disks.Count == 0) { return; } _step = Step.Accounts; Log.Write("installer", "account setup"); }
            else if (_step == Step.Accounts)
            {
                InstallationProfile.ValidateNames(_values[0], _values[1]);
                if (_values[2].Length == 0 || _values[4].Length == 0 || _values[2] != _values[3] || _values[4] != _values[5])
                { throw new ArgumentException("Enter both passwords and matching confirmations."); }
                _step = Step.Confirm; _erase = false; Log.Write("installer", "review disk " + _disks[_selected].Name);
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
