using System;
using System.Threading.Tasks;
using Zenith.Core.Security;
using Zenith.Core.Sessions;
using Zenith.Core.Packages;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

[CollectionDefinition("Appearance", DisableParallelization = true)]
public class AppearanceCollection { }

[Collection("Appearance")]
public class LoginAppearanceTests
{
    [Fact]
    public async Task AuthenticationCompletesOnceAndNeverStartsParallelAttempts()
    {
        var done = new TaskCompletionSource(); int calls = 0;
        var prompt = new LoginPrompt((name, password) => { calls++; done.Task.GetAwaiter().GetResult(); return name == "pilot" && password == "secret" ? new Credentials(1000, 1000) : null; });
        prompt.Submit("pilot", "secret"); prompt.Submit("root", "wrong");
        Assert.True(prompt.Busy); Assert.False(prompt.TakeResult(out _)); done.SetResult();
        Credentials? identity = null;
        for (int i = 0; i < 100 && !prompt.TakeResult(out identity); i++) { await Task.Delay(10); }
        Assert.Equal(1000u, identity!.Uid); Assert.Equal(1, calls); Assert.False(prompt.Busy); Assert.False(prompt.TakeResult(out _));
    }
    [Fact]
    public async Task AuthenticationFailureDoesNotYieldIdentityOrExposePassword()
    {
        var prompt = new LoginPrompt((_, _) => null); prompt.Submit("pilot", "private-value");
        bool completed = false;
        for (int i = 0; i < 200 && !completed; i++) { await Task.Delay(10); completed = prompt.TakeResult(out var id); Assert.Null(id); }
        Assert.True(completed); Assert.False(prompt.Busy); Assert.DoesNotContain("private-value", prompt.Message);
    }
    [Theory]
    [InlineData("VERSION=2\nAccent=FF00AAFF\n")]
    [InlineData("VERSION=1\nAccent=00000000\n")]
    [InlineData("VERSION=1\nAccent=FF1C2030\n")]
    [InlineData("VERSION=1\nTextPrimary=FF1C2030\n")]
    [InlineData("VERSION=1\nUnexpected=value\n")]
    [InlineData("VERSION=1\nAccent=FF00AAFF\nAccent=FF001122\n")]
    public void InvalidThemeReturnsReadableRecoverySnapshot(string text)
    {
        Assert.False(Theme.Apply(text)); Assert.Equal("Zenith recovery", Theme.Name); Assert.Equal(0xFFE8EAF2, Theme.TextPrimary);
    }
    [Fact]
    public void ThemeUpgradeChangesSnapshotAndMissingThemeRestoresFallback()
    {
        Assert.True(Theme.Apply("VERSION=1\nNAME=Test Sunrise\nAccent=FFFFBC70\n"));
        Assert.Equal("Test Sunrise", Theme.Name); Assert.Equal(0xFFFFBC70, Theme.Accent);
        Assert.True(Theme.Apply(null)); Assert.Equal(0xFF7C9CFF, Theme.Accent);
    }
    [Fact]
    public void MetaPackagesContainOnlyDependenciesAndOldDataStillRequiresFiles()
    {
        string text = "ZENITH-PACKAGE\t3\nrepository\tcore\nname\tzenith-style\nversion\t0.1.0\narchitecture\tany\nkind\tmeta\nruntime\tbootstrap-1\nrequires\tzenith-fonts\t>=0.1.0\n";
        var manifest = PackageManifest.Parse(text);
        Assert.Equal("meta", manifest.Kind); Assert.Empty(manifest.Files); Assert.Equal(0, manifest.PayloadBytes);
        Assert.Throws<FormatException>(() => PackageManifest.Parse(text.Replace("\t3\n", "\t2\n")));
        Assert.Throws<FormatException>(() => PackageManifest.Parse(text.Replace("kind\tmeta", "kind\tdata")));
        Assert.Throws<FormatException>(() => PackageManifest.Parse(text+"file\t/usr/share/example\t0644\t0\t"+PackagePlanner.Digest(Array.Empty<byte>())+"\n"));
    }
}
