using System;
using Zenith.Core.Network.Transport;

namespace Zenith.Tests;

public sealed class ClockCorrectionTests
{
    [Fact]
    public void SuccessiveCorrectionsApplyOnceAndContinueWithRawClock()
    {
        long original = ClockCorrection.Ticks;
        try
        {
            long raw = new DateTime(2026, 10, 11).Ticks;
            long first = new DateTime(2026, 10, 3).Ticks;
            ClockCorrection.Synchronize(first, ClockCorrection.Adjust(raw));
            Assert.Equal(first, ClockCorrection.Adjust(raw));
            Assert.Equal(first + TimeSpan.TicksPerSecond, ClockCorrection.Adjust(raw + TimeSpan.TicksPerSecond));
            ClockCorrection.Synchronize(raw, ClockCorrection.Adjust(raw));
            Assert.Equal(raw, ClockCorrection.Adjust(raw));
            Assert.Equal(0, ClockCorrection.Adjust(0));
        }
        finally
        {
            ClockCorrection.Synchronize(100 + original, ClockCorrection.Adjust(100));
        }
    }
}
