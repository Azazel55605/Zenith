using System.Collections.Generic;
using System.Linq;
using Zenith.Core.Sessions;

namespace Zenith.Tests;

/// <summary>Sunset's rules (docs/components/SUNSET.md): what can be chosen, keys, focus, wording, unlock attempts.</summary>
public sealed class SunsetTests
{
    private static readonly SessionBlocker Unsaved = new("Editor — notes.txt", "has unsaved changes", true, null);

    [Fact]
    public void LateInputCannotExtendTheIdleDeadlineOrPasswordGrace()
    {
        var idle = new IdleLockTimer();
        Assert.Null(idle.Poll(10, 60, false)); Assert.Null(idle.Poll(25, 60, true));
        Assert.Null(idle.Poll(84, 60, false));
        Assert.Equal(85L, idle.Poll(120, 60, true));
        Assert.False(IdleLockTimer.CanWake(120, 85 + 15));
        Assert.True(IdleLockTimer.CanWake(99, 100)); Assert.False(IdleLockTimer.CanWake(100, 100));
        Assert.False(IdleLockTimer.CanWake(0, 0));
        idle.Reset(); Assert.Null(idle.Poll(999, 60, false));
        Assert.Null(idle.Poll(9999, 0, false)); Assert.Null(idle.Poll(20000, 0, true));
        Assert.Null(idle.Poll(1, 60, true));
    }

    [Fact]
    public void NothingInTheWayAllowsEverything()
    {
        var choices = SunsetPlan.Choices(new List<SessionBlocker>(), null, null, null);
        Assert.Equal(new[] { 'L', 'O', 'R', 'S' }, choices.Select(c => c.Key));
        Assert.All(choices, c => Assert.Null(c.Disabled));
        Assert.Equal("Everything is saved. Nothing is running.", SunsetPlan.Summary(0));
    }

    [Fact]
    public void BlockersStopEverythingButLock()
    {
        var choices = SunsetPlan.Choices(new List<SessionBlocker> { Unsaved }, null, null, null);
        Assert.Null(choices[0].Disabled);
        Assert.All(choices.Skip(1), c => Assert.NotNull(c.Disabled));
        Assert.Equal(0, SunsetPlan.Initial(choices, SunsetAction.ShutDown));
        Assert.Equal(0, SunsetPlan.Move(choices, 0, 1));
        Assert.Equal("One thing would be lost or interrupted", SunsetPlan.Summary(1));
        Assert.Equal("Two things would be lost or interrupted", SunsetPlan.Summary(2));
    }

    [Fact]
    public void FocusStartsOnLockOrTheAskedActionAndSkipsDisabled()
    {
        var all = SunsetPlan.Choices(new List<SessionBlocker>(), null, null, null);
        Assert.Equal(0, SunsetPlan.Initial(all, SunsetAction.Lock));
        Assert.Equal(2, SunsetPlan.Initial(all, SunsetAction.Restart));
        Assert.Equal(3, SunsetPlan.Move(all, 0, -1));
        var noLock = SunsetPlan.Choices(new List<SessionBlocker>(), "set a password", "set up accounts", null);
        Assert.Equal(2, SunsetPlan.Initial(noLock, SunsetAction.Lock));
        Assert.Equal(3, SunsetPlan.Move(noLock, 2, 1));
        Assert.Equal(2, SunsetPlan.Move(noLock, 3, 1));
        var none = SunsetPlan.Choices(new List<SessionBlocker> { Unsaved }, "no password", null, null);
        Assert.Equal(1, SunsetPlan.Move(none, 1, 1));
    }

    [Fact]
    public void KeysChooseInEitherCase()
    {
        var choices = SunsetPlan.Choices(new List<SessionBlocker>(), null, null, null);
        Assert.Equal(0, SunsetPlan.ForKey(choices, 'l'));
        Assert.Equal(1, SunsetPlan.ForKey(choices, 'O'));
        Assert.Equal(3, SunsetPlan.ForKey(choices, 's'));
        Assert.Equal(-1, SunsetPlan.ForKey(choices, 'x'));
    }

    [Theory]
    [InlineData(40, "up 40 s")]
    [InlineData(720, "up 12 min")]
    [InlineData(3600, "up 1 h")]
    [InlineData(11520, "up 3 h 12 min")]
    [InlineData(187200, "up 2 d 4 h")]
    [InlineData(-5, "up 0 s")]
    public void Uptime(long seconds, string text) => Assert.Equal(text, SunsetPlan.Uptime(seconds));

    [Fact]
    public void LockAttemptsSlowDownAfterFourAndResetOnSuccess()
    {
        var policy = new ScreenLockPolicy();
        for (int i = 0; i < ScreenLockPolicy.FreeAttempts; i++) { policy.Failed(100); Assert.Equal(0, policy.Wait(100)); }
        policy.Failed(100); Assert.Equal(30, policy.Wait(100)); Assert.Equal(0, policy.Wait(130));
        policy.Failed(130); Assert.Equal(60, policy.Wait(130));
        for (int i = 0; i < 20; i++) { policy.Failed(200); }
        Assert.Equal(ScreenLockPolicy.MaximumSeconds, policy.Wait(200));
        policy.Succeeded(); Assert.Equal(0, policy.Wait(200)); Assert.Equal(0, policy.Failures);
    }
}
