using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Xunit;
using Zenith.Core.Configuration;
using Zenith.Core.Processes;
using Zenith.Core.Security;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

/// <summary>The cursor packs (kernel 0.0.71): Comet and Contrast derived from the Meridian images, every pack at the
/// chosen size, and the pack and size settings through the style catalogue.</summary>
public class CursorPackTests
{
    [Fact]
    public void CometTakesTheAccentWithAWhiteRim()
    {
        uint[] light = CursorPacks.Get(CursorPacks.Light, CursorShape.Default, 48, 0, out _, out _);
        uint[] comet = CursorPacks.Get(CursorPacks.Comet, CursorShape.Default, 48, 0x7ECAC7, out int hx, out int hy);
        Assert.Equal((8, 6), (hx, hy));
        // The arrow's body (white in Light) is teal in Comet; no opaque near-black outline remains.
        int body = Array.FindIndex(light, p => p == 0xFFFFFFFFu);
        Assert.True(body >= 0);
        Assert.Equal(0xFF7ECAC7u, comet[body]);
        Assert.DoesNotContain(comet, p => p >> 24 == 255 && (p & 0xFFFFFF) < 0x202020);
        Assert.NotSame(comet, CursorPacks.Get(CursorPacks.Comet, CursorShape.Default, 48, 0xB7A3F0, out _, out _));
    }

    [Fact]
    public void ContrastIsPureLargerAndHasAHeavierOutline()
    {
        Assert.Equal(36, CursorPacks.Logical(CursorPacks.ContrastWhite, 0));
        Assert.Equal(72, CursorPacks.Logical(CursorPacks.ContrastBlack, 2));
        Assert.Equal(32, CursorPacks.Logical(CursorPacks.Comet, 1));
        uint[] white = CursorPacks.Get(CursorPacks.ContrastWhite, CursorShape.Default, 36, 0, out _, out _);
        Assert.All(white, p => Assert.True(p == 0 || p == 0xFFFFFFFFu || p == 0xFF000000u));
        uint[] light = CursorPacks.Get(CursorPacks.Light, CursorShape.Default, 36, 0, out _, out _);
        int Opaque(uint[] a) => a.Count(p => p >> 24 == 255);
        Assert.True(Opaque(white) > Opaque(light));                                    // grown outward
        uint[] black = CursorPacks.Get(CursorPacks.ContrastBlack, CursorShape.Default, 36, 0, out _, out _);
        Assert.Contains(black, p => p == 0xFF000000u); Assert.Contains(black, p => p == 0xFFFFFFFFu);
    }

    [Fact]
    public void EveryPackScalesToAnySizeWithItsHotspot()
    {
        foreach (int size in new[] { 24, 32, 48, 64, 72, 96 })
        {
            uint[] image = CursorPacks.Get(CursorPacks.Light, CursorShape.Text, size, 0, out int hx, out int hy);
            Assert.Equal(size * size, image.Length);
            Assert.Equal((size / 2, size / 2), (hx, hy));
            Assert.Contains(image, p => p >> 24 > 200);
        }
    }

    [Fact]
    public void SoundLevelAndEventsSaveAndSurviveReboot()
    {
        var store = new ProcessSystemSettingsTests.Store(); var service = new SettingsService(store, _ => { }, _ => { }, _ => true);
        Assert.Empty(service.Load());
        Assert.Equal(70U, service.SoundLevel); Assert.Equal(0, (int)(service.SoundMask >> 9 & 3));   // trash and screenshot off
        var (_, revision) = service.Snapshot();
        Assert.Equal(-13, service.SaveSounds(50, 0xFFF, revision));
        using var root = CredentialContext.Enter(Credentials.Root);
        Assert.Equal(-22, service.SaveSounds(101, 0, revision));
        Assert.Equal(0, service.SaveSounds(50, 0xFFF & ~1u, revision));
        Assert.Equal(-11, service.SaveSounds(40, 0, revision));                                 // stale revision
        var reboot = new SettingsService(store, _ => { }, _ => { }, _ => true); Assert.Empty(reboot.Load());
        Assert.Equal(50U, reboot.SoundLevel); Assert.Equal(0xFFEU, reboot.SoundMask);           // sign in off, the rest on
    }
    [Fact]
    public void PackAndSizeAreSettingsTheCatalogueLists()
    {
        var store = new ProcessSystemSettingsTests.Store(); var service = new SettingsService(store, _ => { }, _ => { }, _ => true);
        using var root = CredentialContext.Enter(Credentials.Root);
        byte[] list = StyleCatalog.List(service, 8, 0)!;
        Assert.Equal(5U, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(8)));
        Assert.Equal("Comet", Encoding.UTF8.GetString(list, 32 + 4 * 64 + 24, 5));
        byte[] request = new byte[32]; request[0] = 2; request[4] = 8; request[8] = 1; request[16] = 4;   // Comet
        Assert.Equal(0, ProcessSystemSettings.Save(service, request, true)); Assert.Equal(4U, service.Current.Cursor);
        var data = ProcessSystemSettings.Snapshot(service, true);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(112)));                       // older Meridian: two variants
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(116)));                       // Comet reads as Light there
        request[4] = 9; request[8] = 2; request[16] = 2;                                                    // size: 48 px
        Assert.Equal(0, ProcessSystemSettings.Save(service, request, true)); Assert.Equal(2U, service.Style(9));
        request[8] = 3; request[16] = 3; Assert.Equal(-22, ProcessSystemSettings.Save(service, request, true));
        var reboot = new SettingsService(store, _ => { }, _ => { }, _ => true); Assert.Empty(reboot.Load());
        Assert.Equal(4U, reboot.Current.Cursor); Assert.Equal(2U, reboot.Style(9));
    }
}
