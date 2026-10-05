using System;
using System.Collections.Generic;
using Zenith.Core;
using Zenith.Gui.Graphics;

namespace Zenith.Gui.Shell;

/// <summary>The popup above the launcher button: the registered apps, then the power actions.</summary>
internal sealed class LauncherMenu
{
    private const int Width = 260;
    private const int Padding = 6;
    private const int AppItemHeight = 46;
    private const int PowerItemHeight = 34;
    private const int SeparatorHeight = 9;

    private readonly List<(Rect Rect, Action Run)> _items = new();
    private readonly WindowManager _windows;

    public LauncherMenu(Rect anchor, WindowManager windows)
    {
        _windows = windows;

        int height = 2 * Padding + AppRegistry.Apps.Count * AppItemHeight + SeparatorHeight + 2 * PowerItemHeight;
        Bounds = new Rect(anchor.X - 6, anchor.Y - height - 14, Width, height);
    }

    public Rect Bounds { get; }
    public bool IsOpen { get; set; }

    /// <summary>Handles a click while open. Always closes the menu; runs the item under the pointer, if any.</summary>
    public void HandleClick(int x, int y)
    {
        IsOpen = false;
        foreach (var (rect, run) in _items)
        {
            if (rect.Contains(x, y))
            {
                run();
                return;
            }
        }
    }

    /// <summary>A value that changes whenever the pointer crosses into a different menu item.</summary>
    public int HoverKey(int x, int y)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Rect.Contains(x, y))
            {
                return i + 1;
            }
        }

        return 0;
    }

    public void Draw(Surface surface, int mouseX, int mouseY)
    {
        _items.Clear();
        surface.DrawShadow(Bounds, 12, 18, 100, 6);
        surface.FillRoundRect(Bounds, 12, Theme.Menu);
        surface.StrokeRoundRect(Bounds, 12, Theme.PanelBorder);

        int y = Bounds.Y + Padding;
        int x = Bounds.X + Padding;
        int w = Bounds.W - 2 * Padding;

        foreach (AppInfo app in AppRegistry.Apps)
        {
            Rect item = new(x, y, w, AppItemHeight);
            DrawHover(surface, item, mouseX, mouseY);

            // Monogram tile.
            Rect icon = new(item.X + 8, item.Y + 7, 32, 32);
            surface.FillRoundRect(icon, 8, Theme.AccentSoft);
            surface.DrawTextCentered(app.Name.Substring(0, 1), Fonts.SemiBold, 15, Theme.Accent, icon, true);

            int textX = icon.Right + 12;
            surface.DrawText(app.Name, Fonts.SemiBold, Theme.TextTitle, Theme.TextPrimary, textX, item.Y + 6);
            surface.DrawText(app.Description, Fonts.Regular, Theme.TextSmall, Theme.TextSecondary, textX, item.Y + 24);

            AppInfo captured = app;
            _items.Add((item, () => _windows.Open(captured.Create())));
            y += AppItemHeight;
        }

        surface.FillRect(new Rect(x + 8, y + SeparatorHeight / 2, w - 16, 1), Theme.TitleDivider);
        y += SeparatorHeight;

        AddPowerItem(surface, new Rect(x, y, w, PowerItemHeight), "Restart", PowerControl.Reboot, mouseX, mouseY);
        y += PowerItemHeight;
        AddPowerItem(surface, new Rect(x, y, w, PowerItemHeight), "Shut down", PowerControl.PowerOff, mouseX, mouseY);
    }

    private void AddPowerItem(Surface surface, Rect item, string label, Action run, int mouseX, int mouseY)
    {
        DrawHover(surface, item, mouseX, mouseY);
        surface.DrawTextCentered(label, Fonts.Regular, Theme.TextTitle, Theme.TextSecondary,
            new Rect(item.X + 14, item.Y, item.W - 14, item.H), false);
        _items.Add((item, run));
    }

    private static void DrawHover(Surface surface, Rect item, int mouseX, int mouseY)
    {
        if (item.Contains(mouseX, mouseY))
        {
            surface.FillRoundRect(item, 8, Theme.Hover);
        }
    }
}
