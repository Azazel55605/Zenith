using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zenith.Core.Time;

/// <summary>When a zone observes daylight saving time.</summary>
internal enum DstRule
{
    None,

    /// <summary>EU: last Sunday of March 01:00 UTC to last Sunday of October 01:00 UTC.</summary>
    Europe,

    /// <summary>US/Canada: second Sunday of March 02:00 local to first Sunday of November 02:00 local.</summary>
    NorthAmerica,

    /// <summary>South-east Australia: first Sunday of October 02:00 local to first Sunday of April 03:00 local.</summary>
    Australia,
}

/// <summary>A time zone: standard offset, optional daylight offset, and the rule that switches between them.</summary>
internal sealed class TimeZoneInfoLite
{
    public TimeZoneInfoLite(string name, string standardName, int standardMinutes, string? daylightName = null, int daylightMinutes = 0, DstRule rule = DstRule.None)
    {
        Name = name;
        StandardName = standardName;
        StandardOffset = TimeSpan.FromMinutes(standardMinutes);
        DaylightName = daylightName ?? standardName;
        DaylightOffset = TimeSpan.FromMinutes(daylightMinutes);
        Rule = rule;
    }

    public string Name { get; }
    public string StandardName { get; }
    public TimeSpan StandardOffset { get; }
    public string DaylightName { get; }
    public TimeSpan DaylightOffset { get; }
    public DstRule Rule { get; }

    public bool IsDaylight(DateTime utc)
    {
        int year = utc.Year;
        switch (Rule)
        {
            case DstRule.Europe:
                return utc >= LastSunday(year, 3).AddHours(1) && utc < LastSunday(year, 10).AddHours(1);
            case DstRule.NorthAmerica:
                return utc >= NthSunday(year, 3, 2).AddHours(2) - StandardOffset
                    && utc < NthSunday(year, 11, 1).AddHours(2) - DaylightOffset;
            case DstRule.Australia:
                // Southern hemisphere: daylight time spans the turn of the year.
                return utc < NthSunday(year, 4, 1).AddHours(3) - DaylightOffset
                    || utc >= NthSunday(year, 10, 1).AddHours(2) - StandardOffset;
            default:
                return false;
        }
    }

    public TimeSpan OffsetAt(DateTime utc) => IsDaylight(utc) ? DaylightOffset : StandardOffset;

    public string AbbreviationAt(DateTime utc) => IsDaylight(utc) ? DaylightName : StandardName;

    private static DateTime LastSunday(int year, int month)
    {
        var day = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return day.AddDays(-(int)day.DayOfWeek);
    }

    private static DateTime NthSunday(int year, int month, int n)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        int toSunday = (7 - (int)first.DayOfWeek) % 7;
        return first.AddDays(toSunday + 7 * (n - 1));
    }
}

/// <summary>
/// Built-in zone table (there is no tz database yet) plus fixed offsets written <c>UTC+2</c>,
/// <c>UTC-5:30</c> or <c>Etc/GMT-2</c> (POSIX sign: <c>Etc/GMT-2</c> is two hours ahead of UTC).
/// </summary>
internal static class TimeZones
{
    private static readonly List<TimeZoneInfoLite> s_zones = new()
    {
        new("UTC", "UTC", 0),
        new("Europe/London", "GMT", 0, "BST", 60, DstRule.Europe),
        new("Europe/Dublin", "GMT", 0, "IST", 60, DstRule.Europe),
        new("Europe/Lisbon", "WET", 0, "WEST", 60, DstRule.Europe),
        new("Europe/Berlin", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Vienna", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Zurich", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Paris", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Amsterdam", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Brussels", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Madrid", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Rome", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Stockholm", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Oslo", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Copenhagen", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Warsaw", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Prague", "CET", 60, "CEST", 120, DstRule.Europe),
        new("Europe/Athens", "EET", 120, "EEST", 180, DstRule.Europe),
        new("Europe/Helsinki", "EET", 120, "EEST", 180, DstRule.Europe),
        new("Europe/Kyiv", "EET", 120, "EEST", 180, DstRule.Europe),
        new("Europe/Istanbul", "TRT", 180),
        new("Europe/Moscow", "MSK", 180),
        new("America/New_York", "EST", -300, "EDT", -240, DstRule.NorthAmerica),
        new("America/Toronto", "EST", -300, "EDT", -240, DstRule.NorthAmerica),
        new("America/Chicago", "CST", -360, "CDT", -300, DstRule.NorthAmerica),
        new("America/Denver", "MST", -420, "MDT", -360, DstRule.NorthAmerica),
        new("America/Phoenix", "MST", -420),
        new("America/Los_Angeles", "PST", -480, "PDT", -420, DstRule.NorthAmerica),
        new("America/Vancouver", "PST", -480, "PDT", -420, DstRule.NorthAmerica),
        new("America/Anchorage", "AKST", -540, "AKDT", -480, DstRule.NorthAmerica),
        new("America/Sao_Paulo", "BRT", -180),
        new("Pacific/Honolulu", "HST", -600),
        new("Asia/Dubai", "GST", 240),
        new("Asia/Kolkata", "IST", 330),
        new("Asia/Shanghai", "CST", 480),
        new("Asia/Singapore", "SGT", 480),
        new("Asia/Tokyo", "JST", 540),
        new("Asia/Seoul", "KST", 540),
        new("Australia/Perth", "AWST", 480),
        new("Australia/Sydney", "AEST", 600, "AEDT", 660, DstRule.Australia),
        new("Australia/Melbourne", "AEST", 600, "AEDT", 660, DstRule.Australia),
        new("Pacific/Auckland", "NZST", 720),
    };

    public static IReadOnlyList<TimeZoneInfoLite> All => s_zones;

    public static TimeZoneInfoLite Utc => s_zones[0];

    public static bool TryFind(string name, out TimeZoneInfoLite zone)
    {
        foreach (TimeZoneInfoLite candidate in s_zones)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                zone = candidate;
                return true;
            }
        }

        return TryParseFixed(name, out zone);
    }

    /// <summary>Parses <c>UTC+2</c>, <c>UTC-05:30</c>, <c>GMT+1</c> or <c>Etc/GMT-2</c> into a fixed-offset zone.</summary>
    private static bool TryParseFixed(string name, out TimeZoneInfoLite zone)
    {
        zone = Utc;
        string text = name.Trim();
        int sign;
        if (text.StartsWith("Etc/GMT", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(7);
            sign = -1;   // POSIX: Etc/GMT-2 means UTC+2
        }
        else if (text.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) || text.StartsWith("GMT", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(3);
            sign = 1;
        }
        else
        {
            return false;
        }

        if (text.Length == 0)
        {
            return true;
        }

        if (text[0] != '+' && text[0] != '-')
        {
            return false;
        }

        if (text[0] == '-')
        {
            sign = -sign;
        }

        string[] parts = text.Substring(1).Split(':');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) || hours > 14
            || parts.Length > 2)
        {
            return false;
        }

        int minutes = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes > 59))
        {
            return false;
        }

        int total = sign * (hours * 60 + minutes);
        int abs = Math.Abs(total);
        string label = "UTC" + (total < 0 ? "-" : "+") + abs / 60 + (abs % 60 != 0 ? ":" + (abs % 60 < 10 ? "0" : "") + abs % 60 : "");
        zone = new TimeZoneInfoLite(label, label, total);
        return true;
    }
}
