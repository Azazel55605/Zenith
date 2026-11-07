using Zenith.Core.Memory;

namespace Zenith.Tests;

public class MemoryPressurePolicyTests
{
    [Fact]
    public void AllocationDebtCollectsBeforeLargeRamIsExhaustedAndExternalGcResetsDebt()
    {
        var policy = new MemoryPressurePolicy();
        ulong budget = MemoryPressurePolicy.AllocationBudget;
        Assert.False(policy.ShouldCollect(4_000_000, 3_000_000, 0, 1000, 100, 0));
        Assert.True(policy.ShouldCollect(4_000_000, 3_000_000, 1, 1000, 100 + budget, 0));
        Assert.False(policy.ShouldCollect(4_000_000, 3_000_000, 999, 1000, 100 + budget, 0));
        Assert.True(policy.ShouldCollect(4_000_000, 3_000_000, 1001, 1000, 100 + budget, 0));
        Assert.False(policy.ShouldCollect(4_000_000, 3_000_000, 2001, 1000, 100 + budget, 1));
        Assert.False(policy.ShouldCollect(4_000_000, 3_000_000, 3001, 1000, 99 + 2 * budget, 1));
        Assert.True(policy.ShouldCollect(4_000_000, 3_000_000, 3001, 1000, 100 + 2 * budget, 1));
    }
    [Fact]
    public void KeepsAReserveForNativeAllocations()
    {
        var policy = new MemoryPressurePolicy();
        Assert.False(policy.ShouldCollect(131072, 16385, 0, 1000));
        Assert.True(policy.ShouldCollect(131072, 16384, 0, 1000));
    }

    [Fact]
    public void ThrottlesRepeatedPressureButChecksAgainAfterAnInterval()
    {
        var policy = new MemoryPressurePolicy();
        Assert.True(policy.ShouldCollect(1000, 100, 500, 1000));
        Assert.False(policy.ShouldCollect(1000, 0, 1499, 1000));
        Assert.True(policy.ShouldCollect(1000, 0, 1500, 1000));
        Assert.False(policy.ShouldCollect(1000, 900, 2500, 1000));
        Assert.True(policy.ShouldCollect(1000, 100, 2500, 1000));
    }

    [Fact]
    public void DoesNotCollectWithUnavailableMetrics()
    {
        var policy = new MemoryPressurePolicy();
        Assert.False(policy.ShouldCollect(0, 0, 0, 1000));
        Assert.False(policy.ShouldCollect(1000, 0, 0, 0));
    }
}
