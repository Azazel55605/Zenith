using System;
using System.Diagnostics;
using Cosmos.Kernel.System.Graphics;
using Zenith.Gui;
using Zenith.Gui.Graphics;

namespace Zenith.Apps;

internal sealed class SystemInfoWindow : Window
{
    private readonly string _display;
    private long _shownSecond = -1;

    public SystemInfoWindow() : base("System", 400, 280)
    {
        Canvas screen = Canvas.GetFullScreen();
        _display = screen.Width + " × " + screen.Height + " @ " + screen.RefreshRate + " Hz";
    }

    private static long UptimeSeconds => Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public override bool Update()
    {
        long second = UptimeSeconds;
        if (second == _shownSecond)
        {
            return false;
        }

        _shownSecond = second;
        return true;
    }

    public override void DrawContent(Surface surface, Rect content)
    {
        int x = content.X + 28;
        int y = content.Y + 22;

        int logo = Fonts.SemiBold.GetLineHeight(Theme.TextHeading);
        Logo.Draw(surface, new Rect(x, y, logo, logo));
        surface.DrawText("Zenith OS", Fonts.SemiBold, Theme.TextHeading, Theme.TextPrimary, x + logo + 12, y);
        surface.DrawText("0.1", Fonts.Regular, Theme.TextBody, Theme.TextSecondary,
            x + logo + 24 + Fonts.SemiBold.MeasureString("Zenith OS", Theme.TextHeading), y + 6);
        y += Fonts.SemiBold.GetLineHeight(Theme.TextHeading) + 14;

        long s = UptimeSeconds;
        string uptime = (s / 3600) + "h " + (s / 60 % 60) + "m " + (s % 60) + "s";

        Row(surface, x, ref y, content.W - 56, "Kernel", "Cosmos Gen 3 (NativeAOT)");
        Row(surface, x, ref y, content.W - 56, "Display", _display);
        Row(surface, x, ref y, content.W - 56, "Uptime", uptime);
        DateTime now = DateTime.Now;
        Row(surface, x, ref y, content.W - 56, "Date", now.Year + "-" + Pad2(now.Month) + "-" + Pad2(now.Day));
    }

    private static void Row(Surface surface, int x, ref int y, int width, string label, string value)
    {
        int lineHeight = Fonts.Regular.GetLineHeight(Theme.TextBody);
        surface.DrawText(label, Fonts.Regular, Theme.TextBody, Theme.TextSecondary, x, y);
        surface.DrawText(value, Fonts.Regular, Theme.TextBody, Theme.TextPrimary, x + 100, y);
        y += lineHeight + 8;
        surface.FillRect(new Rect(x, y - 5, width, 1), Theme.TitleDivider);
    }

    private static string Pad2(int value) => value < 10 ? "0" + value : value.ToString();
}
