using System;
using Zenith.Core.Time;

namespace Zenith.Tests;

public class TimeZoneTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static TimeZoneInfoLite Zone(string name)
    {
        Assert.True(TimeZones.TryFind(name, out TimeZoneInfoLite zone), name);
        return zone;
    }

    [Theory]
    // 2026: EU summer time runs from 29 March 01:00 UTC to 25 October 01:00 UTC.
    [InlineData(2026, 1, 15, 12, 0, 60, "CET")]
    [InlineData(2026, 3, 29, 0, 59, 60, "CET")]
    [InlineData(2026, 3, 29, 1, 0, 120, "CEST")]
    [InlineData(2026, 7, 1, 12, 0, 120, "CEST")]
    [InlineData(2026, 10, 25, 0, 59, 120, "CEST")]
    [InlineData(2026, 10, 25, 1, 0, 60, "CET")]
    public void Berlin(int y, int mo, int d, int h, int mi, int offsetMinutes, string abbreviation)
    {
        TimeZoneInfoLite berlin = Zone("Europe/Berlin");
        DateTime utc = Utc(y, mo, d, h, mi);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), berlin.OffsetAt(utc));
        Assert.Equal(abbreviation, berlin.AbbreviationAt(utc));
    }

    [Theory]
    // 2026: US daylight time runs from 8 March 07:00 UTC (02:00 EST) to 1 November 06:00 UTC (02:00 EDT).
    [InlineData(2026, 3, 8, 6, 59, false)]
    [InlineData(2026, 3, 8, 7, 0, true)]
    [InlineData(2026, 11, 1, 5, 59, true)]
    [InlineData(2026, 11, 1, 6, 0, false)]
    public void NewYork(int y, int mo, int d, int h, int mi, bool daylight)
    {
        Assert.Equal(daylight, Zone("America/New_York").IsDaylight(Utc(y, mo, d, h, mi)));
    }

    [Theory]
    // Southern hemisphere, 2026: daylight time ends Sunday 5 April 03:00 AEDT (4 April 16:00 UTC)
    // and starts again Sunday 4 October 02:00 AEST (3 October 16:00 UTC).
    [InlineData(2026, 1, 10, 0, true)]
    [InlineData(2026, 4, 4, 15, true)]
    [InlineData(2026, 4, 4, 16, false)]
    [InlineData(2026, 7, 1, 0, false)]
    [InlineData(2026, 10, 3, 15, false)]
    [InlineData(2026, 10, 3, 16, true)]
    public void Sydney(int y, int mo, int d, int h, bool daylight)
    {
        Assert.Equal(daylight, Zone("Australia/Sydney").IsDaylight(Utc(y, mo, d, h)));
    }

    [Theory]
    [InlineData("UTC+2", 120, "UTC+2")]
    [InlineData("UTC-5:30", -330, "UTC-5:30")]
    [InlineData("GMT+1", 60, "UTC+1")]
    [InlineData("Etc/GMT-2", 120, "UTC+2")]
    [InlineData("utc", 0, "UTC")]
    [InlineData("europe/berlin", 60, "Europe/Berlin")]
    public void FixedOffsetsAndCaseInsensitiveNames(string name, int offsetMinutes, string canonical)
    {
        TimeZoneInfoLite zone = Zone(name);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), zone.OffsetAt(Utc(2026, 1, 1, 0)));
        Assert.Equal(canonical, zone.Name);
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("UTC+15")]
    [InlineData("UTC+2:75")]
    [InlineData("UTC2")]
    public void RejectsUnknownZones(string name)
    {
        Assert.False(TimeZones.TryFind(name, out _));
    }

    [Fact]
    public void SystemClock_FormatsLikeDate()
    {
        Assert.Equal("Mon Oct  5 14:07:09 CEST 2026", SystemClock.Format(new DateTime(2026, 10, 5, 14, 7, 9), "CEST"));
    }
}
