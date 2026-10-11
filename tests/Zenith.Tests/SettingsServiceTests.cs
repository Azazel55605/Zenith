using System;
using System.Collections.Generic;
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
}
