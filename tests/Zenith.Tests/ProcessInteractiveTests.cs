using Zenith.Core.Processes;
namespace Zenith.Tests;
public class ProcessInteractiveTests
{
    [Fact] public void RequiresOwnedSurfaceAndCannotRenewByReenabling()
    {
        var p = new ProcessInteractive();
        Assert.Equal(-9, p.Enable(false, 0));
        Assert.False(p.Enabled);
        Assert.Equal(0, p.Enable(true, 0));
        Assert.Equal(-16, p.Enable(true, 59999));
        Assert.True(p.Advance(true, 60000, out bool renew)); Assert.True(renew);
        Assert.True(p.Advance(true, 60001, out renew)); Assert.False(renew);
    }
    [Fact] public void RevocationHasFiniteCleanupAndCannotReopenProfile()
    {
        var p = new ProcessInteractive(); p.Enable(true, 0);
        Assert.True(p.Advance(false, 100, out bool renew)); Assert.False(renew);
        Assert.False(p.Advance(true, 101, out renew)); Assert.False(renew);
        Assert.False(p.Advance(false, 2100, out renew)); Assert.False(renew);
    }
    [Fact] public void LegacyQuotaBehaviorIsNotChanged()
    {
        var p = new ProcessInteractive();
        Assert.True(p.Advance(false, long.MaxValue, out bool renew)); Assert.False(renew);
    }
}
