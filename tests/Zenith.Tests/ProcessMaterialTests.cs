using System;
using System.Buffers.Binary;
using Zenith.Core.Processes;
using Zenith.Gui.Graphics;

namespace Zenith.Tests;

public class ProcessMaterialTests
{
    private static byte[] Foreground(int width = 160, int height = 100)
    {
        byte[] b = new byte[width * height * 4];
        for (int i = 0; i < b.Length; i += 4) { b[i + 3] = 255; }
        return b;
    }
    [Fact]
    public void NewOpenIsExplicitAndLegacyFormatCannotBecomeTransparent()
    {
        byte[] r = new byte[48];BinaryPrimitives.WriteUInt32LittleEndian(r, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 1);BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), 160);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), 100);BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(24), 1);
        Assert.True(ProcessGraphicsRequest.TryRead(r, out var parsed));Assert.Equal(2, parsed.Format);
        r[4] = 0;Assert.False(ProcessGraphicsRequest.TryRead(r, out _));r[0] = 1;
        Assert.True(ProcessGraphicsRequest.TryRead(r, out parsed));Assert.Equal(1, parsed.Format);
        var old = new ProcessSurface(160, 100, "old");Assert.Equal(-22, old.PresentMaterials(Foreground(), Array.Empty<ProcessMaterialRegion>()));
        var modern = new ProcessSurface(160, 100, "new", 2);Assert.Equal(-22, modern.Present(Foreground()));
    }
    [Fact]
    public void RegionsRejectOverlapOverflowUnsupportedRolesAndWorkAmplification()
    {
        var good = new ProcessMaterialRegion(0, 0, 80, 100, 8, 3, 2);
        Assert.True(ProcessMaterialRegion.Validate(new[] { good }, 160, 100));
        foreach (var bad in new[] { good with { X = -1 }, good with { Width = int.MaxValue }, good with { Radius = 25 },
            good with { Role = 1 }, good with { Effect = 3 }, good with { Width = 0 } })
        { Assert.False(ProcessMaterialRegion.Validate(new[] { bad }, 160, 100)); }
        Assert.False(ProcessMaterialRegion.Validate(new[] { good, good with { X = 40 } }, 160, 100));
        Assert.False(ProcessMaterialRegion.Validate(new[] { new ProcessMaterialRegion(0, 0, 640, 480, 0, 4, 2) }, 640, 480));
        Assert.False(ProcessMaterialRegion.TryRead(new byte[32], 9, out _));
        byte[] wire = new byte[32];wire[28] = 1;Assert.False(ProcessMaterialRegion.TryRead(wire, 1, out _));
    }
    [Fact]
    public void FramesValidatePremultiplicationScopeAndAtomicOwnership()
    {
        var s = new ProcessSurface(160, 100, "new", 2);byte[] frame = Foreground();
        var regions = new[] { new ProcessMaterialRegion(0, 0, 80, 100, 0, 3, 2) };
        frame[0] = 80;frame[3] = 128;Assert.Equal(0, s.PresentMaterials(frame, regions));
        frame[0] = 200;Assert.Equal(-22, s.PresentMaterials(frame, regions));
        frame[0] = 80;frame[80 * 4 + 3] = 0;Assert.Equal(-22, s.PresentMaterials(frame, regions));
        regions[0] = regions[0] with { X = -1 };int[] pixels = new int[16000];var copied = new ProcessMaterialRegion[8];
        Assert.True(s.CopyFrame(pixels, false, copied, out int count));Assert.Equal(1, count);Assert.Equal(0, copied[0].X);
        Assert.Equal(0x80000050U, (uint)pixels[0]); // failed frames and caller edits never replace the accepted snapshot
        Assert.False(s.CopyFrame(pixels, false, copied, out _));s.Dispose();Assert.Equal(-32, s.PresentMaterials(frame, regions));
    }
    [Fact]
    public void WireLengthsVersionsReservedAndCapabilitiesAreFixed()
    {
        byte[] b = new byte[48];b[0] = 1;Assert.True(ProcessMaterialFrame.TryRead(b, out _));
        b[32] = 9;Assert.False(ProcessMaterialFrame.TryRead(b, out _));b[32] = 0;
        b[40] = 1;Assert.False(ProcessMaterialFrame.TryRead(b, out _));b[40] = 0;
        b[24] = 1;Assert.False(ProcessMaterialFrame.TryRead(b, out _));
        Assert.False(ProcessMaterialFrame.TryRead(new byte[47], out _));
        var caps = ProcessMaterialFrame.Capabilities();Assert.Equal(32, caps.Length);
        Assert.Equal(131072U, BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(16)));
    }
    [Fact]
    public void CachedBlurUsesOnlyLowerSceneAndNeverFadesOpaqueForeground()
    {
        const int w = 160, h = 100;int[] scene = new int[w * h], frame = new int[w * h];
        for (int y = 0; y < h; y++)for (int x = 0; x < w; x++)
        { scene[y * w + x] = (x % 2 == 0) ? unchecked((int)0xff000000) : -1;frame[y * w + x] = x < 80 ? 0 : unchecked((int)0xff112233); }
        var regions = new[] { new ProcessMaterialRegion(0, 0, 80, 100, 0, 3, 2) };
        var composition = new MaterialComposition();var content = new Rect(0, 0, w, h);
        frame[20 * w + 20] = unchecked((int)0xffffffff);
        composition.Capture(scene, w, h, content, regions, 1, 0xff202125);Assert.Equal(1, composition.BlurRebuilds);
        composition.Capture(scene, w, h, content, regions, 1, 0xff202125);Assert.Equal(1, composition.BlurRebuilds);
        var output = (int[])scene.Clone();composition.Compose(frame, output, w, h, content, regions, 1, 0xff202125, 0xff2a2c31, 0xff25262b, 0xff2a2c31);
        Assert.Equal(-1, output[20 * w + 20]);Assert.Equal(unchecked((int)0xff112233), output[90]);
        // Neighboring stripes are diffused rather than merely faded with the window.
        Assert.InRange(Math.Abs((output[40] & 255) - (output[41] & 255)), 0, 6);
        scene[0] = unchecked((int)0xffff0000);composition.Capture(scene, w, h, content, regions, 1, 0xff202125);
        Assert.Equal(2, composition.BlurRebuilds);
        regions[0] = regions[0] with { Effect = 0 };composition.Capture(scene, w, h, content, regions, 1, 0xff202125);
        composition.Compose(frame, output, w, h, content, regions, 1, 0xff202125, 0xff2a2c31, 0xff25262b, 0xff2a2c31);
        Assert.Equal(unchecked((int)0xff25262b), output[40]);
    }
    [Fact]
    public void MovingAndClippedMaterialWindowsRefreshTheirOwnLowerSceneOnly()
    {
        const int size = 100;var scene = new int[size * size];var frame = new int[160 * 100];
        for (int y = 0; y < size; y++)for (int x = 0; x < size; x++)
        { scene[y * size + x] = unchecked((int)0xff000000) | (x << 16) | (y << 8); }
        for (int y = 0; y < 100; y++)for (int x = 80; x < 160; x++) { frame[y * 160 + x] = -1; }
        var regions = new[] { new ProcessMaterialRegion(0, 0, 80, 100, 12, 3, 2) };
        var c = new MaterialComposition();var rect = new Rect(-40, 10, 160, 100);
        c.Capture(scene, size, size, rect, regions, 1, 0xff202125);
        var output = (int[])scene.Clone();c.Compose(frame, output, size, size, rect, regions, 1, 0xff202125, 0xff2a2c31, 0xff25262b, 0xff2a2c31);
        for (int x = 0; x < size; x++) { Assert.Equal(scene[x], output[x]); } // above this window
        Assert.Equal(-1, output[20 * size + 90]);Assert.Equal(1, c.BlurRebuilds);
        c.Capture(scene, size, size, rect.Offset(1, 0), regions, 1, 0xff202125);Assert.Equal(2, c.BlurRebuilds);
        c.Capture(scene, size, size, rect.Offset(1, 0), regions, 1, 0xff202125);Assert.Equal(2, c.BlurRebuilds);
    }
}
