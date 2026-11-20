using Zenith.Core.Configuration;
using Zenith.Core.Notifications;
using Zenith.Core.Security;
namespace Zenith.Tests;
[Collection("Notifications")]
public class NotificationTests
{
    private static long s_now;
    public NotificationTests() { NotificationCenter.Reset(); s_now = 1_000_000; NotificationCenter.Clock = () => s_now; }

    [Fact]
    public void PostsCarryTheSenderAndArriveOnceInOrder()
    {
        Assert.Equal(1, NotificationCenter.Post("orbit", "Changes applied", "2 packages", "package", 1000));
        Assert.Equal(2, NotificationCenter.Post("horizon", "Copy finished", "", "", 1000));
        var first = NotificationCenter.TakeNew();
        Assert.Equal(new[] { "orbit", "horizon" }, first.ConvertAll(n => n.Source));
        Assert.Empty(NotificationCenter.TakeNew());
        Assert.Equal(2, NotificationCenter.History().Count);
    }
    [Theory]
    [InlineData("", "body", "")]
    [InlineData("   ", "", "")]
    [InlineData("ok", "", "Bad Icon")]
    [InlineData("ok", "", "../x")]
    public void EmptyTitlesAndOddIconsAreRefused(string title, string body, string icon) => Assert.Equal(-22, NotificationCenter.Post("p", title, body, icon, 0));
    [Fact]
    public void OversizedTextIsRefusedAndControlCharactersBecomeSpaces()
    {
        Assert.Equal(-22, NotificationCenter.Post("p", new string('t', 65), "", "", 0));
        Assert.Equal(-22, NotificationCenter.Post("p", "t", new string('b', 161), "", 0));
        Assert.True(NotificationCenter.Post("p", "two\nlines\u001b[31m", "", "", 0) > 0);
        Assert.Equal("two lines [31m", NotificationCenter.TakeNew()[0].Title);
    }
    [Fact]
    public void ASenderMayPostFiveTimesInTenSecondsOthersAreNotAffected()
    {
        for (int i = 0; i < 5; i++) { Assert.True(NotificationCenter.Post("noisy", "n" + i, "", "", 0) > 0); }
        Assert.Equal(-16, NotificationCenter.Post("noisy", "again", "", "", 0));
        Assert.True(NotificationCenter.Post("quiet", "fine", "", "", 0) > 0);
        s_now += NotificationCenter.BurstMicros;
        Assert.True(NotificationCenter.Post("noisy", "later", "", "", 0) > 0);
    }
    [Fact]
    public void UnreadCountsUntilThePanelOpensAndTheHistoryCanBeTrimmed()
    {
        int a = NotificationCenter.Post("p", "one", "", "", 0), b = NotificationCenter.Post("q", "two", "", "", 0);
        Assert.Equal(2, NotificationCenter.Unread);
        int revision = NotificationCenter.Revision;
        NotificationCenter.MarkRead(); Assert.Equal(0, NotificationCenter.Unread); Assert.NotEqual(revision, NotificationCenter.Revision);
        Assert.True(NotificationCenter.Remove(a)); Assert.False(NotificationCenter.Remove(a));
        Assert.Equal(new[] { b }, NotificationCenter.History().ConvertAll(n => n.Id));
        NotificationCenter.Post("p", "three", "", "", 0); Assert.Equal(1, NotificationCenter.Unread);
        NotificationCenter.Clear(); Assert.Empty(NotificationCenter.History()); Assert.Equal(0, NotificationCenter.Unread);
    }
    [Fact]
    public void HistoryKeepsTheLastThirtyTwo()
    {
        for (int i = 0; i < 40; i++) { NotificationCenter.Post("s" + i, "t" + i, "", "", 0); }
        var history = NotificationCenter.History();
        Assert.Equal(NotificationCenter.Kept, history.Count);
        Assert.Equal("t8", history[0].Title);
    }
    [Fact]
    public void DoNotDisturbAndPopupTimeSaveAndSurviveReboot()
    {
        var store = new ProcessSystemSettingsTests.Store(); var service = new SettingsService(store, _ => { }, _ => { }, _ => true);
        Assert.Empty(service.Load());
        Assert.False(service.DoNotDisturb); Assert.Equal(1U, service.Popup);
        var (_, revision) = service.Snapshot();
        Assert.Equal(-13, service.SaveNotifications(true, 2, revision));
        using var root = CredentialContext.Enter(Credentials.Root);
        Assert.Equal(-22, service.SaveNotifications(true, 3, revision));
        Assert.Equal(0, service.SaveNotifications(true, 2, revision));
        Assert.Equal(-11, service.SaveNotifications(false, 0, revision));
        var reboot = new SettingsService(store, _ => { }, _ => { }, _ => true); Assert.Empty(reboot.Load());
        Assert.True(reboot.DoNotDisturb); Assert.Equal(2U, reboot.Popup);
    }
}
