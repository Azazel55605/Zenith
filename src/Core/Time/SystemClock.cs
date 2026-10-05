using System;
using System.IO;

namespace Zenith.Core.Time;

/// <summary>
/// Local time for the whole system. The hardware clock (RTC) is read as UTC, as Linux does
/// (and QEMU provides); the zone comes from <c>/etc/timezone</c>.
/// </summary>
internal static class SystemClock
{
    public const string ConfigPath = "/etc/timezone";

    public static TimeZoneInfoLite Zone { get; private set; } = TimeZones.Utc;

    /// <summary>Overridable for tests; the real clock otherwise.</summary>
    public static Func<DateTime> UtcSource { get; set; } = () => DateTime.UtcNow;

    public static DateTime UtcNow => DateTime.SpecifyKind(UtcSource(), DateTimeKind.Utc);

    public static DateTime Now => ToLocal(UtcNow);

    public static DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc + Zone.OffsetAt(utc), DateTimeKind.Unspecified);

    public static string Abbreviation => Zone.AbbreviationAt(UtcNow);

    public static bool TrySetZone(string name)
    {
        if (!TimeZones.TryFind(name, out TimeZoneInfoLite zone))
        {
            return false;
        }

        Zone = zone;
        return true;
    }

    public static void Save() => File.WriteAllText(ConfigPath, Zone.Name + "\n");

    /// <summary>Applies the zone saved in /etc/timezone, if any. Returns an error message for an unknown zone.</summary>
    public static string? LoadSaved()
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        string name = File.ReadAllText(ConfigPath).Trim();
        return name.Length == 0 || TrySetZone(name) ? null : "unknown time zone '" + name + "' in " + ConfigPath;
    }

    /// <summary>Formats like <c>date</c>: <c>Mon Oct  5 14:30:00 CEST 2026</c>.</summary>
    public static string Format(DateTime local, string zoneAbbreviation)
    {
        string[] days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        return days[(int)local.DayOfWeek] + " " + months[local.Month - 1] + " " + local.Day.ToString().PadLeft(2) + " "
            + Pad2(local.Hour) + ":" + Pad2(local.Minute) + ":" + Pad2(local.Second) + " " + zoneAbbreviation + " " + local.Year;
    }

    public static string Pad2(int value) => value < 10 ? "0" + value : value.ToString();
}
