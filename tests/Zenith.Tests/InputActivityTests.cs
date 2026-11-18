using Zenith.Gui;
namespace Zenith.Tests;
public sealed class InputActivityTests
{
    [Fact] public void StationaryMiddlePressRoutesAndInterruptsMotion()
    {
        // HZ5: MouseManager had received the press, but Desktop.Tick skipped
        // HandleInput when the pointer had not moved in that same tick.
        var input = new InputActivity(false, false, false, true, 0, 0);
        Assert.True(input.RoutesWithoutMotion);
        Assert.True(input.InterruptsMotion);
    }
    [Fact] public void ReleaseRoutesWithoutCuttingAnAnimationShort()
    {
        var input = new InputActivity(false, true, false, false, 0, 0);
        Assert.True(input.RoutesWithoutMotion); Assert.False(input.InterruptsMotion);
        var idle = new InputActivity(false, false, false, false, 0, 0);
        Assert.False(idle.RoutesWithoutMotion); Assert.False(idle.InterruptsMotion);
    }
    [Theory]
    [InlineData(true, false, 0, 0)]
    [InlineData(false, true, 0, 0)]
    [InlineData(false, false, -120, 0)]
    [InlineData(false, false, 0, 1)]
    public void OtherExistingActionsStillRoute(bool left, bool right, int scroll, int keys)
    {
        var input = new InputActivity(left, false, right, false, scroll, keys);
        Assert.True(input.RoutesWithoutMotion); Assert.True(input.InterruptsMotion);
    }
}
