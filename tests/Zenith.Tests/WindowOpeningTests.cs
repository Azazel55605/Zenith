using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;
namespace Zenith.Tests;
public class WindowOpeningTests
{
    [Fact]
    public void CompactWindowsScaleToTheScreenKeepingTheirProportions()
    {
        // 1920x1080 less the taskbar and a title bar: limited by four fifths of the height.
        var fit = WindowOpening.Fit(640, 480, 1920, 988, 16384, 16384);
        Assert.NotNull(fit);
        var (w, h) = fit!.Value;
        Assert.True(w > 640 && h > 480);
        Assert.True(h <= 988 * 8 / 10 && w <= 1920 * 3 / 4);
        Assert.InRange(w * 480L - h * 640L, -1000L * 2, 1000L * 2);   // 4:3 within rounding to even pixels
        Assert.Equal(0, w % 2); Assert.Equal(0, h % 2);
        // Meridian asks for 600x480.
        Assert.NotNull(WindowOpening.Fit(600, 480, 1920, 988, 16384, 16384));
    }
    [Fact]
    public void SmallScreensAndSmallWindowsKeepTheRequestedSize()
    {
        Assert.Null(WindowOpening.Fit(640, 480, 800, 560, 16384, 16384));      // no room to grow by a tenth
        Assert.Null(WindowOpening.Fit(640, 480, 1024, 650, 16384, 16384));     // 650*0.8 = 520: under 1.1x
        Assert.Equal((746, 558), WindowOpening.Fit(640, 480, 1024, 700, 16384, 16384));
        Assert.Null(WindowOpening.Fit(320, 200, 3840, 2100, 16384, 16384));    // at most twice: 640x400 is under the resize minimum
        Assert.Null(WindowOpening.Fit(0, 480, 1920, 988, 16384, 16384));
    }
    [Fact]
    public void LargeScreensStopAtTwiceTheRequestAndTheFramebufferLimit()
    {
        Assert.Equal((1280, 960), WindowOpening.Fit(640, 480, 3840, 2100, 16384, 16384));
        Assert.Equal((1000, 960), WindowOpening.Fit(640, 480, 3840, 2100, 1000, 1200));
    }
    [Fact]
    public void AWindowWaitsForAFrameAtTheSizeTheDesktopAskedFor()
    {
        var surface = new ProcessSurface(640, 480, "test");
        Assert.False(surface.PresentedAtRequestedSize);
        Assert.Equal(0, surface.Present(new byte[640 * 480 * 4]));
        Assert.True(surface.PresentedAtRequestedSize);
        Assert.Equal(0, surface.EnableResize());
        surface.RequestResize(1000, 750);
        Assert.False(surface.PresentedAtRequestedSize);                     // the compact frame is not what was asked for
        byte[] request = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), 1000); BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(12), 750);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(16), 2);
        Assert.Equal(0, surface.AcceptResize(request));
        Assert.False(surface.PresentedAtRequestedSize);                     // accepted, no frame at that size yet
        Assert.Equal(0, surface.Present(new byte[1000 * 750 * 4]));
        Assert.True(surface.PresentedAtRequestedSize);
        // A program that refuses keeps its size, and its frame counts.
        var refusing = new ProcessSurface(640, 480, "test"); refusing.EnableResize(); refusing.Present(new byte[640 * 480 * 4]);
        refusing.RequestResize(1000, 750); Assert.False(refusing.PresentedAtRequestedSize);
        Assert.Equal(0, refusing.RefuseResize()); Assert.True(refusing.PresentedAtRequestedSize);
    }
    private sealed class Store : ISettingsStore
    {
        public bool Persistent => true;
        public readonly Dictionary<string, string> Files = new();
        public string? Read(string path) => Files.GetValueOrDefault(path);
        public void Replace(string path, string text) => Files[path] = text;
    }
    [Fact]
    public void TheWindowsSettingIsSavedAndReadBack()
    {
        var store = new Store(); var service = new SettingsService(store, _ => { }, _ => { }, _ => true);
        Assert.Empty(service.Load()); Assert.Equal(0U, service.Current.Windows);   // fit by default
        using (CredentialContext.Enter(new Credentials(1000, 1000)))
        { Assert.Throws<UnauthorizedAccessException>(() => service.SetWindows("compact")); }
        using var root = CredentialContext.Enter(Credentials.Root);
        service.SetWindows("compact"); Assert.Equal(1U, service.Current.Windows);
        Assert.Contains("WINDOWS=compact", store.Files[SettingsService.WindowsPath]);
        Assert.Throws<ArgumentException>(() => service.SetWindows("huge"));
        var reboot = new SettingsService(store, _ => { }, _ => { }, _ => true); Assert.Empty(reboot.Load()); Assert.Equal(1U, reboot.Current.Windows);
        store.Replace(SettingsService.WindowsPath, "WINDOWS=giant\n");
        var broken = new SettingsService(store, _ => { }, _ => { }, _ => true); Assert.Single(broken.Load()); Assert.Equal(0U, broken.Current.Windows);
    }
}
