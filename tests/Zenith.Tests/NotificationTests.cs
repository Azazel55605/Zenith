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
    public void ButtonsAreBoundedAndActivatingOneFinishesTheNotification()
    {
        var open = new NotificationCenter.NoteAction("Show updates", "Orbit", new[] { "updates" });
        int id = NotificationCenter.Post("Orbit", "2 updates are ready", "", "package", 0, 0, new[] { open });
        Assert.Same(open, NotificationCenter.Activate(id, 0));
        Assert.Empty(NotificationCenter.History());
        Assert.Null(NotificationCenter.Activate(id, 0));                                   // gone
        var three = new[] { open, open, open };
        Assert.Equal(-22, NotificationCenter.Post("Orbit", "t", "", "", 0, 0, three));
        Assert.Equal(-22, NotificationCenter.Post("Orbit", "t", "", "", 0, 0, new[] { new NotificationCenter.NoteAction("", "Orbit", new string[0]) }));
        Assert.Equal(-22, NotificationCenter.Post("Orbit", "t", "", "", 0, 0, new[] { new NotificationCenter.NoteAction("Go", "Orbit", new[] { "a\nb" }) }));
    }
    [Fact]
    public void MutedSendersPostNothingButAreStillListed()
    {
        NotificationCenter.IsMuted = name => name == "noisy";
        Assert.Equal(0, NotificationCenter.Post("noisy", "hello", "", "", 0));
        Assert.Empty(NotificationCenter.History()); Assert.Empty(NotificationCenter.TakeNew());
        Assert.True(NotificationCenter.Post("quiet", "hello", "", "", 0, 77) > 0);
        Assert.Equal(new[] { "noisy", "quiet" }, NotificationCenter.Senders());
        Assert.Equal(77, NotificationCenter.History()[0].SenderPid);
    }
    [Fact]
    public void MutedSendersSaveAndSurviveReboot()
    {
        var store = new ProcessSystemSettingsTests.Store(); var service = new SettingsService(store, _ => { }, _ => { }, _ => true);
        Assert.Empty(service.Load());
        using var root = CredentialContext.Enter(Credentials.Root);
        var (_, revision) = service.Snapshot();
        Assert.Equal(-22, service.SaveSenderMuted("a,b", true, revision));
        Assert.Equal(0, service.SaveSenderMuted("Orbit", true, revision));
        (_, revision) = service.Snapshot();
        Assert.Equal(0, service.SaveNotifications(true, 0, revision));                     // keeps the switches
        var reboot = new SettingsService(store, _ => { }, _ => { }, _ => true); Assert.Empty(reboot.Load());
        Assert.Equal(new[] { "Orbit" }, reboot.MutedSenders); Assert.True(reboot.DoNotDisturb);
        (_, revision) = reboot.Snapshot();
        Assert.Equal(0, reboot.SaveSenderMuted("Orbit", false, revision)); Assert.Empty(reboot.MutedSenders);
    }
    [Fact]
    public void TheDiskWarningComesOnceAndAgainOnlyAfterSpaceRecovered()
    {
        Zenith.Core.Storage.StorageWatch.Reset();
        ulong free = 100UL << 20, total = 4UL << 30;
        Zenith.Core.Storage.StorageWatch.Root = () => (free, total);
        long t = 0;
        Assert.True(Zenith.Core.Storage.StorageWatch.Check(t));
        Assert.False(Zenith.Core.Storage.StorageWatch.Check(t += 1));                       // not yet a minute
        Assert.False(Zenith.Core.Storage.StorageWatch.Check(t += Zenith.Core.Storage.StorageWatch.IntervalMicros));   // still low: once
        free = 300UL << 20;                                                                  // above 256 MiB but below 10 %
        Assert.False(Zenith.Core.Storage.StorageWatch.Check(t += Zenith.Core.Storage.StorageWatch.IntervalMicros));
        free = 1UL << 30;                                                                    // recovered
        Assert.False(Zenith.Core.Storage.StorageWatch.Check(t += Zenith.Core.Storage.StorageWatch.IntervalMicros));
        free = 100UL << 20;
        Assert.True(Zenith.Core.Storage.StorageWatch.Check(t += Zenith.Core.Storage.StorageWatch.IntervalMicros));
        var note = NotificationCenter.History()[^1];
        Assert.Equal("Disk almost full", note.Title); Assert.Contains("100 MB", note.Body); Assert.Equal(2, note.Actions!.Length);
        Zenith.Core.Storage.StorageWatch.Root = () => null; Zenith.Core.Storage.StorageWatch.Reset();
    }
    [Fact]
    public void ASmallDiskThatIsMostlyEmptyDoesNotWarn()
    {
        Zenith.Core.Storage.StorageWatch.Reset();
        Zenith.Core.Storage.StorageWatch.Root = () => (100UL << 20, 256UL << 20);        // 39 % free of a 256 MiB disk
        Assert.False(Zenith.Core.Storage.StorageWatch.Check(0));
        Zenith.Core.Storage.StorageWatch.Reset();
        Zenith.Core.Storage.StorageWatch.Root = () => (20UL << 20, 2UL << 40);           // 20 MiB left: always too little
        Assert.True(Zenith.Core.Storage.StorageWatch.Check(0));
        Zenith.Core.Storage.StorageWatch.Root = () => null; Zenith.Core.Storage.StorageWatch.Reset();
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
