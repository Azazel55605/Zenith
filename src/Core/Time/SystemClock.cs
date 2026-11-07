using System;
using Zenith.Core.Network.Transport;

namespace Zenith.Core.Time;

/// <summary>
/// Local time for the whole system. The hardware clock (RTC) is read as UTC, as Linux does
/// (and QEMU provides); the zone comes from <c>/etc/timezone</c>.
/// </summary>
internal static class SystemClock
{
    public static TimeZoneInfoLite Zone { get; private set; } = TimeZones.Utc;

    /// <summary>Overridable for tests; the real clock otherwise.</summary>
    public static Func<DateTime> UtcSource { get; set; } = () => HardwareToUtc(DateTime.UtcNow);

    /// <summary>The hardware clock keeps local time (VMware does by default), not UTC.</summary>
    public static bool HardwareClockIsLocal { get; set; }
    public static string Source { get; set; } = "RTC (UTC unless rtc=local is explicit)";

    /// <summary>The RTC value as UTC: unchanged for a UTC clock; for a local-time clock, minus the zone's
    /// offset at that moment. Around a DST change the local hour is ambiguous; the standard offset wins.</summary>
    public static DateTime HardwareToUtc(DateTime rtc)
    {
        if (!HardwareClockIsLocal) { return rtc; }
        DateTime guess = rtc - Zone.StandardOffset;
        return rtc - Zone.OffsetAt(guess);
    }

    public static DateTime UtcNow => DateTime.SpecifyKind(UtcSource(), DateTimeKind.Utc);
    public static void Synchronize(DateTime utc)
    {
        ClockCorrection.Synchronize(utc.Ticks, UtcSource().Ticks);
    }

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
