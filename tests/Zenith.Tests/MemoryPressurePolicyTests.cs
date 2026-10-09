using Zenith.Core.Memory;

namespace Zenith.Tests;

public class MemoryPressurePolicyTests
{
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
