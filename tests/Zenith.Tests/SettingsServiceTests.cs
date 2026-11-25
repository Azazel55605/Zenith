using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Zenith.Core.Processes;
using System.IO;
using Zenith.Core.Configuration;
using Zenith.Core.Security;
using Zenith.Core.Sessions;

namespace Zenith.Tests;

public class SettingsServiceTests
{
    private sealed class Store : ISettingsStore
    {
        public readonly Dictionary<string, string> Files = new();
        public bool FailWrite;
        public bool Persistent => true;
        public string? Read(string path) => Files.GetValueOrDefault(path);
        public void Replace(string path, string text)
        { if (FailWrite) { throw new IOException("read-only filesystem"); } Files[path] = text; }
    }
    private static SettingsService Service(Store store, Action<string>? keyboard = null) =>
        new(store, keyboard ?? (_ => { }), _ => { }, mode => mode != SessionMode.WindowManager);

    [Fact]
    public void LegacySettingsLoadWithCanonicalAliasesAndNoBootWrites()
    {
        var store = new Store(); store.Files[SettingsService.KeyboardPath] = "# keep\nKEYMAP=\"uk\"\nFONT=large\n";
        store.Files[SettingsService.TimeZonePath] = "Europe/Berlin\n";
        store.Files[SettingsService.SessionPath] = "SESSION=headless\n";
        var service = Service(store); Assert.Empty(service.Load());
        Assert.Equal(new SystemSettings("gb", "Europe/Berlin", SessionMode.Headless), service.Current);
        Assert.Equal("SESSION=headless\n", store.Files[SettingsService.SessionPath]);
    }
    [Theory]
    [InlineData("KEYMAP=unknown\n")]
    [InlineData("KEYMAP=de\nKEYMAP=us\n")]
    [InlineData("KEYMAP=\"de\n")]
    [InlineData("KEYMAP=de\0\n")]
    public void InvalidKeyboardFallsBackAndLeavesOtherSettingsUsable(string text)
    {
        var store = new Store(); store.Files[SettingsService.KeyboardPath] = text;
        store.Files[SettingsService.TimeZonePath] = "UTC+2\n";
        var service = Service(store); Assert.Single(service.Load());
        Assert.Equal("us", service.Current.Keyboard); Assert.Equal("UTC+2", service.Current.TimeZone);
    }
    [Fact]
    public void BoundsApplyToUtf8Bytes()
    {
        var store = new Store(); store.Files[SettingsService.KeyboardPath] = "#" + new string('é', 9000);
        Assert.Contains("oversized", Assert.Single(Service(store).Load()));
    }
    [Fact]
    public void SavePreservesUnrelatedKeysAndPersistsBeforeApplying()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store(); store.Files[SettingsService.KeyboardPath] = "# keep\nFONT=large\nKEYMAP=us\n";
        var service = Service(store, name => Assert.Contains("KEYMAP=" + name, store.Files[SettingsService.KeyboardPath]));
        service.SetKeyboard("de-latin1");
        Assert.Equal("de", service.Current.Keyboard);
        Assert.Equal("# keep\nFONT=large\nKEYMAP=de\n", store.Files[SettingsService.KeyboardPath]);
        var reboot = Service(store); Assert.Empty(reboot.Load()); Assert.Equal("de", reboot.Current.Keyboard);
    }
    [Fact]
    public void FailedSaveDoesNotApplyOrChangeSnapshot()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store { FailWrite = true }; int applied = 0;
        var service = Service(store, _ => applied++);
        Assert.Throws<IOException>(() => service.SetKeyboard("de"));
        Assert.Equal(0, applied); Assert.Equal("us", service.Current.Keyboard); Assert.Empty(store.Files);
    }
    [Fact]
    public void OrdinaryUserCannotWriteAnySystemSetting()
    {
        using var user = CredentialContext.Enter(new Credentials(1000, 1000));
        var store = new Store(); var service = Service(store);
        Assert.Throws<UnauthorizedAccessException>(() => service.SetKeyboard("de"));
        Assert.Throws<UnauthorizedAccessException>(() => service.SetTimeZone("Europe/Berlin"));
        Assert.Throws<UnauthorizedAccessException>(() => service.SetSession(SessionMode.Desktop));
        Assert.Empty(store.Files);
    }
    [Fact]
    public void SessionMigrationPreservesExtraKeysButFutureVersionIsNotDowngraded()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store(); store.Files[SettingsService.SessionPath] = "# legacy\nSESSION=de\nCUSTOM=keep\n";
        var service = Service(store); service.SetSession(SessionMode.Headless);
        Assert.Equal("# legacy\nCUSTOM=keep\nVERSION=1\nSESSION=headless\n", store.Files[SettingsService.SessionPath]);
        store.Files[SettingsService.SessionPath] = "VERSION=2\nSESSION=de\n";
        Assert.Throws<FormatException>(() => service.SetSession(SessionMode.Desktop));
        Assert.Equal("VERSION=2\nSESSION=de\n", store.Files[SettingsService.SessionPath]);
        Assert.Single(service.Load()); Assert.Equal(SessionMode.Desktop, service.Current.DefaultSession);
    }
    [Fact]
    public void UnsupportedSessionAndInvalidTimezoneLeaveSavedStateAlone()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store(); var service = Service(store);
        Assert.Throws<InvalidOperationException>(() => service.SetSession(SessionMode.WindowManager));
        Assert.Throws<ArgumentException>(() => service.SetTimeZone("unknown")); Assert.Empty(store.Files);
    }
    [Fact]
    public void SessionSelectionSeparatesSavedDefaultOverrideAndCapabilities()
    {
        var selected = SessionModes.Select(SessionMode.Desktop, "other=1 session=headless", _ => true);
        Assert.Equal(SessionMode.Headless, selected.Mode); Assert.Equal("boot argument", selected.Source);
        selected = SessionModes.Select(SessionMode.Headless, "", mode => mode == SessionMode.Desktop);
        Assert.Equal(SessionMode.Desktop, selected.Mode); Assert.Contains("unavailable", selected.Diagnostic);
        selected = SessionModes.Select(SessionMode.Headless, "session=de session=wm", _ => true);
        Assert.Equal(SessionMode.Headless, selected.Mode); Assert.Contains("duplicate", selected.Diagnostic);
        Assert.Throws<InvalidOperationException>(() => SessionModes.Select(SessionMode.Desktop, "", _ => false));
    }
    [Fact]
    public void DesktopPagesPersistAndARejectedDraftNeverChangesLiveSettings()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store();
        store.Files[SettingsService.TaskbarPath] = "VERSION=1\nTASKBAR=always\nCUSTOM=keep\n";
        var settings = Service(store);
        Assert.Empty(settings.Load());
        Assert.Equal(1u, settings.Current.Taskbar);
        var (original, _, revision) = settings.DesktopSnapshot();
        Assert.Equal(-22, settings.SaveDesktopPreferences(1, new uint[] { 4, 1 },
            new DesktopEntry[] { new(0, "Terminal"), new(0, "terminal") }, revision));
        store.FailWrite = true;
        Assert.Equal(-5, settings.SaveDesktopPreferences(1, new uint[] { 4, 1 },
            new DesktopEntry[] { new(0, "Terminal") }, revision));
        Assert.Same(original, settings.DesktopPreferences);
        Assert.Equal(1u, settings.Current.Taskbar);
        store.FailWrite = false;
        Assert.Equal(0, settings.SaveDesktopPreferences(1, new uint[] { 4, 1 },
            new DesktopEntry[] { new(0, "Terminal"), new(0, "Editor") }, revision));
        Assert.Contains("CUSTOM=keep", store.Files[SettingsService.TaskbarPath]);

        var desktops = new DesktopEntry[] { new(2, "Développement"), new(0, "Desktop 1") };
        Assert.Equal(-11, settings.SaveDesktopPreferences(4, new uint[] { 0 }, desktops, revision));
        Assert.Equal(0, settings.SaveDesktopPreferences(4, new uint[] { 0 }, desktops, ++revision));
        Assert.Equal(0, settings.SaveDesktopPreferences(2, new uint[] { 1, 400, 0, 1, 2, 3, 0, 28 }, Array.Empty<DesktopEntry>(), ++revision));
        Assert.Equal(0, settings.SaveDesktopPreferences(3, new uint[] { 1, 2, 1, 1 }, Array.Empty<DesktopEntry>(), ++revision));

        var reopened = Service(store);
        Assert.Empty(reopened.Load());
        Assert.Equal(4u, reopened.Current.Taskbar);
        Assert.Equal(1u, reopened.DesktopPreferences.TaskbarPosition);
        Assert.Equal(new[] { "Terminal", "Editor" }, reopened.DesktopPreferences.TaskbarPins);
        Assert.Equal(desktops, reopened.DesktopPreferences.Desktops);
        Assert.Equal(0u, reopened.DesktopPreferences.DesktopMotion);
        Assert.Equal(400u, reopened.DesktopPreferences.FocusDelay);
        Assert.Equal(28u, reopened.DesktopPreferences.WindowSnapDistance);
        Assert.Equal(2u, reopened.DesktopPreferences.SwitcherSort);
        Assert.Equal(1u, reopened.DesktopPreferences.SwitcherScope);
    }

    [Fact]
    public void TilingSplitPersistsWithoutOverwritingWorkspaceSettings()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store();
        store.Files[SettingsService.DesktopPath] = "VERSION=1\nMASTER_PERCENT=90\nCUSTOM=keep\n";
        var service = Service(store);
        Assert.Contains(service.Load(), error => error.Contains("MASTER_PERCENT"));
        Assert.Equal(55, service.DesktopPreferences.MasterPercent);
        var (_, _, revision) = service.DesktopSnapshot();
        using (CredentialContext.Enter(new Credentials(1000, 1000)))
        {
            Assert.Throws<UnauthorizedAccessException>(() => service.SetTileSplit("65"));
        }
        Assert.Throws<ArgumentException>(() => service.SetTileSplit("24"));
        store.FailWrite = true;
        Assert.Throws<IOException>(() => service.SetTileSplit("65"));
        Assert.Equal(revision, service.DesktopSnapshot().Revision);
        store.FailWrite = false;
        service.SetTileSplit("65");
        var desktop = new DesktopEntry[] { new(0, "Main"), new(1, "Code") };
        Assert.Equal(-11, service.SaveDesktopPreferences(4, new uint[] { 0 }, desktop, revision));
        Assert.Equal(0, service.SaveDesktopPreferences(4, new uint[] { 0 }, desktop, revision + 1));
        var reopened = Service(store);
        Assert.Empty(reopened.Load());
        Assert.Equal(65, reopened.DesktopPreferences.MasterPercent);
        Assert.Equal(desktop, reopened.DesktopPreferences.Desktops);
        Assert.Contains("CUSTOM=keep", store.Files[SettingsService.DesktopPath]);
    }

    [Fact]
    public void DesktopWireEnforcesAuthorizationCanonicalPaddingAndRevision()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var service = Service(new Store());
        byte[] data = ProcessDesktopPreferences.Snapshot(service, 4, true);
        Assert.Equal(-22, ProcessDesktopPreferences.Save(service, data)); // read-only response flags cannot be submitted
        data[8] = 0;
        using (CredentialContext.Enter(new Credentials(1000, 1000)))
        { Assert.Equal(-13, ProcessDesktopPreferences.Save(service, data)); }
        data[70] = 0;
        Assert.Equal(-22, ProcessDesktopPreferences.Save(service, data)); // premature NUL leaves nonzero padding
        data[70] = (byte)'s';
        data[72] = 0xff;
        Assert.Equal(-22, ProcessDesktopPreferences.Save(service, data));
        // Use a fresh record to verify the successful and stale paths.
        data = ProcessDesktopPreferences.Snapshot(service, 4, false);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0);
        Assert.Equal(0, ProcessDesktopPreferences.Save(service, data));
        Assert.Equal(-11, ProcessDesktopPreferences.Save(service, data));
        Assert.Equal(0u, service.DesktopPreferences.DesktopMotion);
    }

    [Fact]
    public void ExtendedWindowDraftAndLegacyClientsShareOneRevisionAndPreserveGaps()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store();
        store.Files[SettingsService.DesktopPath] = "MASTER_PERCENT=65\nCUSTOM=keep\n";
        var service = Service(store);
        Assert.Empty(service.Load());
        byte[] draft = ProcessDesktopPreferences.Snapshot(service, 5, false);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(draft.AsSpan(56)));
        service.SetTileGaps("4", "8");
        Assert.Equal(-11, ProcessDesktopPreferences.Save(service, draft));

        draft = ProcessDesktopPreferences.Snapshot(service, 5, false);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(draft.AsSpan(56)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(draft.AsSpan(60)));
        BinaryPrimitives.WriteUInt32LittleEndian(draft.AsSpan(28), 500); // Hover delay.
        BinaryPrimitives.WriteUInt32LittleEndian(draft.AsSpan(56), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(draft.AsSpan(60), 33);
        Assert.Equal(-22, ProcessDesktopPreferences.Save(service, draft));
        BinaryPrimitives.WriteUInt32LittleEndian(draft.AsSpan(60), 0);
        using (CredentialContext.Enter(new Credentials(1000, 1000)))
        {
            Assert.Equal(-13, ProcessDesktopPreferences.Save(service, draft));
            Assert.Throws<UnauthorizedAccessException>(() => service.SetTileGaps("0", "32"));
        }
        store.FailWrite = true;
        Assert.Equal(-5, ProcessDesktopPreferences.Save(service, draft));
        Assert.Equal((250u, 4, 8), (service.DesktopPreferences.FocusDelay, service.DesktopPreferences.TileInnerGap, service.DesktopPreferences.TileOuterGap));
        store.FailWrite = false;
        Assert.Equal(0, ProcessDesktopPreferences.Save(service, draft));
        Assert.Equal(-11, ProcessDesktopPreferences.Save(service, draft));
        byte[] legacy = ProcessDesktopPreferences.Snapshot(service, 2, false);
        Assert.Equal(500u, BinaryPrimitives.ReadUInt32LittleEndian(legacy.AsSpan(28)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(legacy.AsSpan(56)));
        legacy[60] = 1;
        Assert.Equal(-22, ProcessDesktopPreferences.Save(service, legacy));
        legacy[60] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(28), 600);
        Assert.Equal(0, ProcessDesktopPreferences.Save(service, legacy));
        var reopened = Service(store);
        Assert.Empty(reopened.Load());
        Assert.Equal((600u, 32, 0), (reopened.DesktopPreferences.FocusDelay, reopened.DesktopPreferences.TileInnerGap, reopened.DesktopPreferences.TileOuterGap));
        Assert.Equal(65, reopened.DesktopPreferences.MasterPercent);
        Assert.Contains("CUSTOM=keep", store.Files[SettingsService.DesktopPath]);
    }

    [Fact]
    public void MalformedGapPairFallsBackAndInvalidCliWritesLeaveSettingsIntact()
    {
        using var root = CredentialContext.Enter(Credentials.Root);
        var store = new Store();
        const string original = "VERSION=1\nMASTER_PERCENT=60\nWM_GAPS=10,-1\n";
        store.Files[SettingsService.DesktopPath] = original;
        var service = Service(store);
        Assert.Contains(service.Load(), error => error.Contains("WM_GAPS"));
        Assert.Equal((60, 0, 0), (service.DesktopPreferences.MasterPercent, service.DesktopPreferences.TileInnerGap, service.DesktopPreferences.TileOuterGap));
        Assert.Throws<ArgumentException>(() => service.SetTileGaps("33", "1"));
        Assert.Throws<ArgumentException>(() => service.SetTileGaps("1", "-1"));
        Assert.Equal(original, store.Files[SettingsService.DesktopPath]);
        store.Files[SettingsService.DesktopPath] = "VERSION=2\n";
        Assert.Throws<FormatException>(() => service.SetTileGaps("0", "1"));
        Assert.Equal("VERSION=2\n", store.Files[SettingsService.DesktopPath]);
    }
}
